using System.Threading.Tasks;
using Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace API.Extensions
{
    public static class AzureSearchInitializationExtensions
    {
        public static async Task InitializeAzureSearchIndexAsync(this IHost host)
        {
            using var scope = host.Services.CreateScope();
            var searchService = scope.ServiceProvider.GetRequiredService<IAzureAiSearchService>();
            await searchService.InitializeIndexAsync();
        }

        public static async Task SyncProductsToAzureSearchAsync(this IHost host)
        {
            using var scope = host.Services.CreateScope();
            var searchService = scope.ServiceProvider.GetRequiredService<IAzureAiSearchService>();
            await searchService.SyncProductsAsync();
        }

        public static async Task SyncRagDocumentsToAzureSearchAsync(this IHost host)
        {
            using var scope = host.Services.CreateScope();
            var searchService = scope.ServiceProvider.GetRequiredService<IAzureAiSearchService>();
            await searchService.SyncRagDocumentsAsync();
        }
    }
}
