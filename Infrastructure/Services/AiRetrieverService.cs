using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;

namespace Infrastructure.Services
{
    /*
     * End-to-end example of how data changes in this file:
     *
     * 1. Raw database data might look like this:
     *    Name = "Green React Woolen Hat"
     *    Brand = "React"
     *    Type = "Hat"
     *    Price = 8
     *
     * 2. AiDocumentService flattens that into one AI document:
     *    Title = "Green React Woolen Hat"
     *    Text = "Product: Green React Woolen Hat. Brand: React. Type: Hat. Price: $8."
     *    Metadata = { brand = "React", type = "Hat", price = "8.00" }
     *
     * 3. This retriever turns that document into chunks:
     *    Chunk A = full text
     *    Chunk B = "Product: Green React Woolen Hat."
     *    Chunk C = "Brand: React."
     *    Chunk D = "Type: Hat."
     *    Chunk E = "Price: $8."
     *
     * 4. If the user asks "hat under 10", the question is also turned into a vector.
     *
     * 5. Every chunk is turned into a vector too, and we compare them using cosine similarity.
     *
     * 6. Chunks that look most similar to the question get the highest score and are returned.
     *
     * Easy idea:
     * raw DB row -> flattened document -> chunks -> vectors -> similarity scores -> best chunks
     */
    public class AiRetrieverService : IAiRetrieverService
    {
        // This fixed vector size keeps the retriever self-contained for now.
        // Later we can replace these local vectors with real embeddings from an AI provider.
        private const int VectorSize = 128;

        private readonly IAiDocumentService _aiDocumentService;

        public AiRetrieverService(IAiDocumentService aiDocumentService)
        {
            _aiDocumentService = aiDocumentService;
        }

        public async Task<IReadOnlyList<AiSearchMatch>> SearchAsync(string question, int maxResults = 5)
        {
            // If the question is empty, there is nothing useful to search.
            if (string.IsNullOrWhiteSpace(question))
            {
                return Array.Empty<AiSearchMatch>();
            }

            // Load the full AI documents first.
            // Example shape of "documents":
            // [
            //   {
            //     Id = "product-8",
            //     Title = "Green React Woolen Hat",
            //     Text = "Product: Green React Woolen Hat. Brand: React. Type: Hat. Price: $8.",
            //     Metadata = { brand = "React", type = "Hat", price = "8.00" }
            //   }
            // ]
            var documents = await _aiDocumentService.GetDocumentsAsync();

            // Break those documents into smaller chunks.
            // Smaller chunks usually give more precise retrieval than one large block of text.
            // Example shape of "chunks":
            // [
            //   { Id = "product-8-full", Text = "Product: Green React Woolen Hat. Brand: React. Type: Hat. Price: $8." },
            //   { Id = "product-8-chunk-1", Text = "Product: Green React Woolen Hat." },
            //   { Id = "product-8-chunk-2", Text = "Brand: React." },
            //   { Id = "product-8-chunk-3", Text = "Type: Hat." },
            //   { Id = "product-8-chunk-4", Text = "Price: $8." }
            // ]
            var chunks = documents.SelectMany(CreateChunks).ToList();

            // Turn the user's question into numbers so we can compare it with chunk vectors.
            // Example shape of "queryVector":
            // [0, 0.35, 0, 0.12, ...]
            // Each position is a numeric bucket representing part of the question text.
            var queryVector = Vectorize(question);

            // Final returned shape:
            // [
            //   { Chunk = { Title = "Green React Woolen Hat", Text = "Type: Hat." }, Score = 0.81 },
            //   { Chunk = { Title = "Green React Woolen Hat", Text = "Price: $8." }, Score = 0.76 }
            // ]
            return chunks
                .Select(chunk => new AiSearchMatch
                {
                    Chunk = chunk,
                    Score = CosineSimilarity(queryVector, Vectorize(BuildSearchText(chunk)))
                })
                .Where(match => match.Score > 0)
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Chunk.Title)
                .Take(maxResults)
                .ToList();
        }

        private static IReadOnlyList<AiDocumentChunk> CreateChunks(AiDocument document)
        {
            // Split one big document into smaller chunks so retrieval can return the most relevant part.
            // Example: one product document becomes a full chunk plus sentence-level chunks.
            // Example shape of "document":
            // {
            //   Id = "product-8",
            //   Title = "Green React Woolen Hat",
            //   Text = "Product: Green React Woolen Hat. Brand: React. Type: Hat. Price: $8."
            // }
            var chunks = new List<AiDocumentChunk>
            {
                new AiDocumentChunk
                {
                    Id = $"{document.Id}-full",
                    DocumentId = document.Id,
                    SourceType = document.SourceType,
                    Title = document.Title,
                    Text = document.Text,
                    Metadata = new Dictionary<string, string>(document.Metadata)
                }
            };

            // Regex note:
            // (?<=[.!?]) means "split after a sentence-ending mark"
            // \s+ means "one or more spaces"
            // So this turns a paragraph into sentence-sized pieces.
            // Example shape of "sentences":
            // [
            //   "Product: Green React Woolen Hat.",
            //   "Brand: React.",
            //   "Type: Hat.",
            //   "Price: $8."
            // ]
            var sentences = Regex.Split(document.Text, @"(?<=[.!?])\s+")
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();

            for (var index = 0; index < sentences.Count; index++)
            {
                chunks.Add(new AiDocumentChunk
                {
                    Id = $"{document.Id}-chunk-{index + 1}",
                    DocumentId = document.Id,
                    SourceType = document.SourceType,
                    Title = document.Title,
                    Text = sentences[index].Trim(),
                    Metadata = new Dictionary<string, string>(document.Metadata)
                });
            }

            return chunks;
        }

        private static string BuildSearchText(AiDocumentChunk chunk)
        {
            // Combine title, text, and metadata into one search string.
            // This helps terms like brand or price still affect retrieval.
            // Example input "chunk":
            // {
            //   Title = "Green React Woolen Hat",
            //   Text = "Type: Hat.",
            //   Metadata = { brand = "React", type = "Hat", price = "8.00", url = "/shop/8" }
            // }
            //
            // Example result:
            // "Green React Woolen Hat Type: Hat. React Hat 8.00 /shop/8"
            return $"{chunk.Title} {chunk.Text} {string.Join(" ", chunk.Metadata.Values)}";
        }

        private static double[] Vectorize(string text)
        {
            // Convert text into a simple numeric vector using hashed tokens.
            // Example: "react hat under 20" becomes token counts spread across vector buckets.
            // Easy idea:
            // a "vector" is just a list of numbers.
            // We turn words into numbers so math can compare two pieces of text.
            // Example input "text":
            // "Green React Woolen Hat Type: Hat. React Hat 8.00 /shop/8"
            //
            // Example output shape:
            // [0, 0.18, 0, 0.36, 0, 0.18, ...]
            // This is not human-readable meaning anymore.
            // It is a numeric version of the text that the retriever can compare mathematically.
            var vector = new double[VectorSize];

            // Regex note:
            // [a-z0-9]+ means "read one word/number at a time"
            // Example shape of "tokens" for "hat under 10":
            // ["hat", "under", "10"]
            var tokens = Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+")
                .Select(match => match.Value);

            foreach (var token in tokens)
            {
                // Hashing puts each word into one bucket in the vector.
                // Example: "react" might go to bucket 17, "hat" to bucket 82.
                var bucket = Math.Abs(token.GetHashCode()) % VectorSize;
                vector[bucket] += 1d;
            }

            // Normalize the vector so longer text does not automatically win just because it has more words.
            return Normalize(vector);
        }

        private static double[] Normalize(double[] vector)
        {
            // Magnitude means the overall size/length of the vector.
            // We divide by it so vectors are easier to compare fairly.
            var magnitude = Math.Sqrt(vector.Sum(value => value * value));
            if (magnitude <= 0)
            {
                return vector;
            }

            for (var index = 0; index < vector.Length; index++)
            {
                vector[index] /= magnitude;
            }

            return vector;
        }

        private static double CosineSimilarity(IReadOnlyList<double> left, IReadOnlyList<double> right)
        {
            // Cosine similarity tells us how similar two vectors point in the same direction.
            // Easy idea:
            // - high score = question and chunk are talking about similar things
            // - low score = they are not very related
            // Example:
            // "cheap hats" should be closer to a hat product chunk than to a delivery chunk.
            // What are "left" and "right" here?
            // - left = question vector
            // - right = one chunk vector
            //
            // Example:
            // left  = [0.2, 0.8, 0.0, 0.4]
            // right = [0.1, 0.7, 0.0, 0.5]
            //
            // We multiply matching positions and add them:
            // (0.2 * 0.1) + (0.8 * 0.7) + (0.0 * 0.0) + (0.4 * 0.5)
            //
            // Why?
            // Because matching positions mean the same kinds of words/features are strong in both vectors.
            // If many positions line up well, the final score becomes higher.
            // Example:
            // left  = [0.2, 0.8, 0.0, 0.4]
            // right = [0.1, 0.7, 0.0, 0.5]
            // score = (0.2*0.1) + (0.8*0.7) + (0.0*0.0) + (0.4*0.5)
            //       = 0.02 + 0.56 + 0.00 + 0.20
            //       = 0.78
            //
            // Compare that with a weak match:
            // left  = [0.2, 0.8, 0.0, 0.4]
            // right = [0.9, 0.1, 0.3, 0.0]
            // score = (0.2*0.9) + (0.8*0.1) + (0.0*0.3) + (0.4*0.0)
            //       = 0.18 + 0.08 + 0.00 + 0.00
            //       = 0.26
            //
            // So 0.78 is a stronger match than 0.26 because more important positions align well.
            double sum = 0;

            for (var index = 0; index < left.Count; index++)
            {
                // Multiply the values in the same bucket position.
                // If both vectors are strong in the same place, this adds more to the score.
                sum += left[index] * right[index];
            }

            // Return the final similarity score.
            // Higher score = more similar meaning.
            return sum;
        }
    }
}
