using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Models;

namespace Core.Interfaces
{
    public interface IAiRetrieverService
    {
        // Search for the most relevant chunks for a question.
        // Example: question = "show me hats under 20"
        Task<IReadOnlyList<AiSearchMatch>> SearchAsync(string question, int maxResults = 5);
    }
}
