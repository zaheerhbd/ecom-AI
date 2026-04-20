using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Core.Interfaces;

namespace Infrastructure.Services
{
    public class LocalHashEmbeddingService : IAiEmbeddingService
    {
        // This is a simple local fallback size.
        // It lets the app keep working even when a real OpenAI key is not configured yet.
        private const int VectorSize = 128;

        public Task<IReadOnlyList<IReadOnlyList<double>>> CreateEmbeddingsAsync(IReadOnlyList<string> inputs)
        {
            // Example input:
            // [
            //   "hat under 10",
            //   "Green React Woolen Hat Type: Hat. React Hat 8.00 /shop/8"
            // ]
            //
            // Example output shape:
            // [
            //   [0, 0.22, 0, 0.44, ...],
            //   [0.11, 0, 0.33, 0.22, ...]
            // ]
            var vectors = inputs
                .Select(Vectorize)
                .Cast<IReadOnlyList<double>>()
                .ToList();

            return Task.FromResult<IReadOnlyList<IReadOnlyList<double>>>(vectors);
        }

private static double[] Vectorize(string text)
{
    // Turns text into a fixed-size numeric array (vector).
    // Example:
    // "red shoes sale"
    // becomes something like:
    // [0, 1, 0, 2, 0, 1, ...]
    //
    // The exact numbers are not important to a person.
    // What matters is:
    // similar text produces similar vectors.

    var vector = new double[VectorSize];

    // Break the text into simple lowercase words/tokens.
    // Example:
    // "Red Shoes, Sale!" -> "red", "shoes", "sale"
    //
    // If text is null, use an empty string so the code does not fail.
    var tokens = Regex.Matches((text ?? string.Empty).ToLowerInvariant(), "[a-z0-9]+")
        .Select(match => match.Value);

    foreach (var token in tokens)
    {
        // Put each token into one position ("bucket") in the vector.
        //
        // Example idea:
        // token = "red"   -> bucket 12
        // token = "shoes" -> bucket 245
        // token = "sale"  -> bucket 12
        //
        // If two words land in the same bucket, that bucket count increases.
        // This is a simple hashing approach to represent text as numbers.
        var bucket = Math.Abs(token.GetHashCode()) % VectorSize;

        // Increase the count for that bucket.
        // Example:
        // if bucket 12 gets "red" and "sale",
        // then vector[12] may become 2.
        vector[bucket] += 1d;
    }

    // Normalize makes the vector scale consistent.
    // This helps compare short text and long text more fairly.
    // Example:
    // "red shoes" and "red shoes red shoes"
    // should still look similar after normalization.
    return Normalize(vector);
}


        private static double[] Normalize(double[] vector)
        {
            // Normalizing keeps long text from winning only because it has more words.
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
    }
}
