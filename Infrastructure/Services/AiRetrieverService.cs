using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Core.Interfaces;
using Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services
{
    public class AiRetrieverService : IAiRetrieverService
    {
        private const int EmbeddingPreviewLength = 8;
        private const string DocumentEmbeddingCacheKey = "AiDocumentEmbeddingCache";

        private readonly IAiDocumentService _aiDocumentService;
        private readonly IAiEmbeddingService _aiEmbeddingService;
        private readonly IConfiguration _config;
        private readonly ILogger<AiRetrieverService> _logger;
        private readonly IMemoryCache _memoryCache;

        public AiRetrieverService(
            IAiDocumentService aiDocumentService,
            IAiEmbeddingService aiEmbeddingService,
            IConfiguration config,
            ILogger<AiRetrieverService> logger,
            IMemoryCache memoryCache)
        {
            _aiDocumentService = aiDocumentService;
            _aiEmbeddingService = aiEmbeddingService;
            _config = config;
            _logger = logger;
            _memoryCache = memoryCache;
        }

        public async Task<IReadOnlyList<AiSearchMatch>> SearchAsync(
            string question,
            int maxResults = 5,
            string preferredSourceType = null,
            double? maximumPrice = null)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                return Array.Empty<AiSearchMatch>();
            }

            var azureMatches = await SearchAzureAsync(question, maxResults, preferredSourceType, maximumPrice);
            if (azureMatches.Count > 0)
            {
                return azureMatches;
            }

            _logger.LogInformation("Azure RAG search returned no matches or is unavailable. Falling back to in-memory retrieval.");

            return await SearchInMemoryAsync(question, maxResults, preferredSourceType, maximumPrice);
        }

        private async Task<IReadOnlyList<AiSearchMatch>> SearchAzureAsync(
            string question,
            int maxResults,
            string preferredSourceType,
            double? maximumPrice)
        {
            var endpoint = _config["AzureSearch:Endpoint"];
            var apiKey = _config["AzureSearch:ApiKey"];
            var ragIndexName = _config["AzureSearch:RagIndexName"];

            if (string.IsNullOrWhiteSpace(endpoint)
                || string.IsNullOrWhiteSpace(apiKey)
                || string.IsNullOrWhiteSpace(ragIndexName))
            {
                return Array.Empty<AiSearchMatch>();
            }

            var questionEmbeddingResult = await _aiEmbeddingService.CreateEmbeddingsAsync(new[] { question });
            if (questionEmbeddingResult == null || questionEmbeddingResult.Count == 0)
            {
                return Array.Empty<AiSearchMatch>();
            }

            var queryVector = questionEmbeddingResult[0].Select(value => (float)value).ToArray();

            try
            {
                var searchClient = new SearchClient(
                    new Uri(endpoint),
                    ragIndexName,
                    new AzureKeyCredential(apiKey));

                var searchOptions = new SearchOptions
                {
                    Size = maxResults
                };

                var filters = new List<string>();

                if (!string.IsNullOrWhiteSpace(preferredSourceType))
                {
                    filters.Add($"sourceType eq '{preferredSourceType}'");
                }

                if (maximumPrice.HasValue)
                {
                    filters.Add($"price lt {maximumPrice.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                if (filters.Count > 0)
                {
                    searchOptions.Filter = string.Join(" and ", filters);
                }

                searchOptions.Select.Add("id");
                searchOptions.Select.Add("sourceType");
                searchOptions.Select.Add("sourceId");
                searchOptions.Select.Add("title");
                searchOptions.Select.Add("content");
                searchOptions.Select.Add("brand");
                searchOptions.Select.Add("category");
                searchOptions.Select.Add("price");
                searchOptions.Select.Add("url");

                searchOptions.VectorSearch = new VectorSearchOptions();
                searchOptions.VectorSearch.Queries.Add(
                    new VectorizedQuery(queryVector)
                    {
                        KNearestNeighborsCount = maxResults,
                        Exhaustive = true,
                    });
                searchOptions.VectorSearch.Queries[0].Fields.Add("contentVector");

                var response = await searchClient.SearchAsync<SearchDocument>("*", searchOptions);
                var matches = new List<AiSearchMatch>();

                await foreach (var result in response.Value.GetResultsAsync())
                {
                    matches.Add(new AiSearchMatch
                    {
                        Chunk = ToChunk(result.Document),
                        Score = result.Score ?? 0
                    });
                }

                return matches;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Azure RAG retrieval failed. Falling back to in-memory retrieval.");
                return Array.Empty<AiSearchMatch>();
            }
        }

        private async Task<IReadOnlyList<AiSearchMatch>> SearchInMemoryAsync(
            string question,
            int maxResults,
            string preferredSourceType,
            double? maximumPrice)
        {
            var cacheEntry = await GetDocumentEmbeddingCacheAsync();
            if (cacheEntry.Chunks.Count == 0)
            {
                return Array.Empty<AiSearchMatch>();
            }

            var questionEmbeddingResult = await _aiEmbeddingService.CreateEmbeddingsAsync(new[] { question });
            if (questionEmbeddingResult == null || questionEmbeddingResult.Count == 0)
            {
                return Array.Empty<AiSearchMatch>();
            }

            var queryVector = questionEmbeddingResult[0];

            LogEmbeddingPreview(question, cacheEntry.Chunks, cacheEntry.DocumentEmbeddings.Prepend(queryVector).ToList());

            var candidateMatches = cacheEntry.Chunks.Select((chunk, index) => new AiSearchMatch
                {
                    Chunk = chunk,
                    Score = CosineSimilarity(queryVector, cacheEntry.DocumentEmbeddings[index])
                });

            if (!string.IsNullOrWhiteSpace(preferredSourceType))
            {
                candidateMatches = candidateMatches.Where(match =>
                    string.Equals(match.Chunk.SourceType, preferredSourceType, StringComparison.OrdinalIgnoreCase));
            }

            if (maximumPrice.HasValue)
            {
                candidateMatches = candidateMatches.Where(match =>
                    TryGetMetadataDouble(match.Chunk, "price", out var price) && price < maximumPrice.Value);
            }

            return candidateMatches
                .Where(match => match.Score > 0)
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Chunk.Title)
                .Take(maxResults)
                .ToList();
        }

        private static AiDocumentChunk ToChunk(SearchDocument document)
        {
            var metadata = new Dictionary<string, string>();

            AddMetadata(metadata, "sourceId", GetString(document, "sourceId"));
            AddMetadata(metadata, "brand", GetString(document, "brand"));
            AddMetadata(metadata, "type", GetString(document, "category"));
            AddMetadata(metadata, "price", GetString(document, "price"));
            AddMetadata(metadata, "url", GetString(document, "url"));

            return new AiDocumentChunk
            {
                Id = GetString(document, "id"),
                DocumentId = GetString(document, "id"),
                SourceType = GetString(document, "sourceType"),
                Title = GetString(document, "title"),
                Text = GetString(document, "content"),
                Metadata = metadata
            };
        }

        private static string GetString(SearchDocument document, string key)
        {
            if (!document.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value.ToString();
        }

        private static void AddMetadata(IDictionary<string, string> metadata, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value;
            }
        }

        private static bool TryGetMetadataDouble(AiDocumentChunk chunk, string key, out double value)
        {
            value = 0;

            return chunk.Metadata.TryGetValue(key, out var rawValue)
                && double.TryParse(rawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private async Task<DocumentEmbeddingCacheEntry> GetDocumentEmbeddingCacheAsync()
        {
            return await _memoryCache.GetOrCreateAsync(DocumentEmbeddingCacheKey, async cacheEntry =>
            {
                // Step 1: Set cache to expire in 1 hour
                cacheEntry.SetAbsoluteExpiration(TimeSpan.FromHours(1));

                // Step 2: Load all documents from database
                var documents = await _aiDocumentService.GetDocumentsAsync();

                // Step 3: Convert documents into searchable chunks
                var chunks = documents.Select(CreateChunk).ToList();

                // Step 4: Build search text for each chunk
                var textsToEmbed = chunks.Select(BuildSearchText).ToList();

                // Step 5: Create embeddings for all search texts
                var embeddings = await _aiEmbeddingService.CreateEmbeddingsAsync(textsToEmbed);

                // Step 6: Handle embedding count mismatch (fallback to empty vectors)
                if (embeddings.Count != chunks.Count)
                {
                    _logger.LogWarning("AI document embeddings count mismatch: expected {Expected} but got {Actual}.", chunks.Count, embeddings.Count);
                    embeddings = chunks.Select(_ => (IReadOnlyList<double>)Array.Empty<double>()).ToList();
                }

                // Step 7: Return the cache entry with chunks and embeddings
                return new DocumentEmbeddingCacheEntry(chunks, embeddings);
            });
        }

        private void LogEmbeddingPreview(
            string question,
            IReadOnlyList<AiDocumentChunk> chunks,
            IReadOnlyList<IReadOnlyList<double>> embeddings)
        {
            _logger.LogInformation(
                "AI embedding preview for question \"{Question}\" => dimensions={Dimensions}, preview=[{Preview}]",
                question,
                embeddings[0].Count,
                FormatEmbeddingPreview(embeddings[0]));

            for (var index = 0; index < chunks.Count; index++)
            {
                var chunk = chunks[index];
                var embedding = embeddings[index + 1];

                _logger.LogInformation(
                    "AI document embedding => chunkId={ChunkId}, documentId={DocumentId}, sourceType={SourceType}, title={Title}, text={Text}, searchText={SearchText}, dimensions={Dimensions}, preview=[{Preview}]",
                    chunk.Id,
                    chunk.DocumentId,
                    chunk.SourceType,
                    chunk.Title,
                    chunk.Text,
                    BuildSearchText(chunk),
                    embedding.Count,
                    FormatEmbeddingPreview(embedding));
            }
        }

        private static AiDocumentChunk CreateChunk(AiDocument document)
        {
            // Example:
            //   document.Metadata = { ["price"] = "18", ["brand"] = "Acme" }
            // becomes a chunk carrying the same business data forward.
            // Reasoning: copying metadata here keeps useful structured fields available for both search text generation
            // and later UI/source rendering without mutating the original document object.
            return new AiDocumentChunk
            {
                Id = document.Id,
                DocumentId = document.Id,
                SourceType = document.SourceType,
                Title = document.Title,
                Text = document.Text,
                Metadata = new Dictionary<string, string>(document.Metadata)
            };
        }

        private sealed class DocumentEmbeddingCacheEntry
        {
            public DocumentEmbeddingCacheEntry(
                IReadOnlyList<AiDocumentChunk> chunks,
                IReadOnlyList<IReadOnlyList<double>> documentEmbeddings)
            {
                Chunks = chunks;
                DocumentEmbeddings = documentEmbeddings;
            }

            public IReadOnlyList<AiDocumentChunk> Chunks { get; }
            public IReadOnlyList<IReadOnlyList<double>> DocumentEmbeddings { get; }
        }

        private static string BuildSearchText(AiDocumentChunk chunk)
        {
            // Example:
            //   chunk.Title = "Blue Hat"
            //   chunk.Text = "A casual blue hat for daily wear"
            //   chunk.Metadata.Values = ["18", "Acme"]
            // returns:
            //   "Blue Hat A casual blue hat for daily wear 18 Acme"
            // Reasoning: we merge title, body text, and metadata into one searchable sentence so the embedding captures
            // both descriptive language ("blue hat") and structured facts ("18", "Acme").
            return $"{chunk.Title} {chunk.Text} {string.Join(" ", chunk.Metadata.Values)}";
        }

        private static double CosineSimilarity(IReadOnlyList<double> left, IReadOnlyList<double> right)
        {
            // This method multiplies each matching vector position and adds the results into one similarity score.
            // Example:
            //   left  = [0.12, -0.04, 0.91]
            //   right = [0.10, -0.01, 0.88]
            //   score = (0.12*0.10) + (-0.04*-0.01) + (0.91*0.88) = 0.8132
            // Reasoning: one number is easier to rank than comparing many vector dimensions manually, so the retriever
            // can sort chunks from most relevant to least relevant.
            double sum = 0;

            for (var index = 0; index < left.Count; index++)
            {
                sum += left[index] * right[index];
            }

            return sum;
        }

        private static string FormatEmbeddingPreview(IReadOnlyList<double> embedding)
        {
            return string.Join(", ", embedding.Take(EmbeddingPreviewLength).Select(value => value.ToString("0.000000")));
        }
    }
}
