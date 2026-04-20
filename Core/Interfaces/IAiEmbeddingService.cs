using System.Collections.Generic;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    public interface IAiEmbeddingService
    {
        // Turn one or more text inputs into embedding vectors.
        // Example:
        // "hat under 10" -> [0.01, -0.03, 0.12, ...]
        Task<IReadOnlyList<IReadOnlyList<double>>> CreateEmbeddingsAsync(IReadOnlyList<string> inputs);
    }
}
