using System;
using System.Linq;
using System.Threading.Tasks;
using API.Extensions;
using Core.Entities.Identity;
using Infrastructure.Data;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Web;

namespace API
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var startupLogger = NLogBuilder.ConfigureNLog("nlog.config").GetCurrentClassLogger();
            try 
            {
                var host = CreateHostBuilder(args).Build();

                var shouldRunMigrations = args.Any(arg => string.Equals(arg, "--migrate", StringComparison.OrdinalIgnoreCase))
                    || string.Equals(Environment.GetEnvironmentVariable("RUN_MIGRATIONS"), "true", StringComparison.OrdinalIgnoreCase);

                if (shouldRunMigrations)
                {
                    await ApplyMigrationsAsync(host);
                }
                else
                {
                    startupLogger.Info("Skipping automatic database migration and seeding at startup. Use --migrate or RUN_MIGRATIONS=true to enable.");
                }

                // Initialize Azure AI Search index
                var shouldInitSearch = args.Any(arg => string.Equals(arg, "--init-search", StringComparison.OrdinalIgnoreCase))
                    || string.Equals(Environment.GetEnvironmentVariable("INIT_SEARCH_INDEX"), "true", StringComparison.OrdinalIgnoreCase);

                if (shouldInitSearch)
                {
                    startupLogger.Info("Initializing Azure AI Search index...");
                    await host.InitializeAzureSearchIndexAsync();
                    startupLogger.Info("Azure AI Search index initialization completed.");
                }

                var shouldSyncProducts = args.Any(arg => string.Equals(arg, "--sync-products", StringComparison.OrdinalIgnoreCase))
                    || string.Equals(Environment.GetEnvironmentVariable("SYNC_PRODUCTS_TO_AZURE_SEARCH"), "true", StringComparison.OrdinalIgnoreCase);

                if (shouldSyncProducts)
                {
                    startupLogger.Info("Syncing products to Azure AI Search...");
                    await host.SyncProductsToAzureSearchAsync();
                    startupLogger.Info("Azure AI Search product sync completed.");
                }

                var shouldSyncRag = args.Any(arg => string.Equals(arg, "--sync-rag", StringComparison.OrdinalIgnoreCase))
                    || string.Equals(Environment.GetEnvironmentVariable("SYNC_RAG_TO_AZURE_SEARCH"), "true", StringComparison.OrdinalIgnoreCase);

                if (shouldSyncRag)
                {
                    startupLogger.Info("Syncing RAG documents to Azure AI Search...");
                    await host.SyncRagDocumentsToAzureSearchAsync();
                    startupLogger.Info("Azure AI Search RAG sync completed.");
                }

                host.Run();
            }
            catch (Exception ex)
            {
                startupLogger.Error(ex, "The API terminated during startup.");
                throw;
            }
            finally
            {
                LogManager.Shutdown();
            }
        }

        private static async Task ApplyMigrationsAsync(IHost host)
        {
            using var scope = host.Services.CreateScope();
            var services = scope.ServiceProvider;
            var loggerFactory = services.GetRequiredService<ILoggerFactory>();

            var context = services.GetRequiredService<StoreContext>();
            await context.Database.MigrateAsync();
            await StoreContextSeed.SeedAsync(context, loggerFactory);
                
            var userManager = services.GetRequiredService<UserManager<AppUser>>();
            var identityContext = services.GetRequiredService<AppIdentityDbContext>();
            await identityContext.Database.MigrateAsync();
            await AppIdentityDbContextSeed.SeedUsersAsync(userManager);
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
                })
                .UseNLog()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
