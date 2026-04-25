using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Models;

namespace Core.Interfaces
{
    public interface IAiRetrieverService
    {
        // Search for the most relevant chunks for a question.
        // Example: question = "show me hats under 20"
        // preferredSourceType narrows the search when we already understand the user's intent.
        // Example: preferredSourceType = "product" for "show me hats", or "policy" for "delivery options".
        // maximumPrice filters price-backed documents for questions like "products under 20 dollars".
        Task<IReadOnlyList<AiSearchMatch>> SearchAsync(
            string question,
            int maxResults = 5,
            string preferredSourceType = null,
            double? maximumPrice = null);
    }
}
