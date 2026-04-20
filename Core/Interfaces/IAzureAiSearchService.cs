using System.Threading.Tasks;

namespace Core.Interfaces
{
    public interface IAzureAiSearchService
    {
        Task InitializeIndexAsync();
        Task SyncProductsAsync();
        Task SyncRagDocumentsAsync();
    }
}
