using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Core.Entities;
using Core.Interfaces;
using Core.Models;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services
{
    // This service prepares data from our app and pushes it into Azure AI Search.
    // Real example:
    // - Product "Nike Air Max" becomes a searchable document with fields like name, brand, category, and price.
    // - RAG content like "Shipping Policy" becomes a searchable document with both text and a vector embedding.
    public class AzureAiSearchService : IAzureAiSearchService
    {
        // Azure Search supports large indexing operations, but sending documents in smaller chunks is safer.
        // Example: if we have 1,200 products, this sends them as 500 + 500 + 200 instead of one huge request.
        private const int BatchSize = 500;

        private readonly IConfiguration _config;
        private readonly StoreContext _context;
        private readonly IAiDocumentService _aiDocumentService;
        private readonly IAiEmbeddingService _aiEmbeddingService;
        private readonly ILogger<AzureAiSearchService> _logger;

        public AzureAiSearchService(
            IConfiguration config,
            StoreContext context,
            IAiDocumentService aiDocumentService,
            IAiEmbeddingService aiEmbeddingService,
            ILogger<AzureAiSearchService> logger)
        {
            _config = config;
            _context = context;
            _aiDocumentService = aiDocumentService;
            _aiEmbeddingService = aiEmbeddingService;
            _logger = logger;
        }

        public async Task InitializeIndexAsync()
        {
            var indexClient = CreateIndexClient();
            var indexName = GetRequiredSetting("AzureSearch:IndexName");

            try
            {
                await indexClient.GetIndexAsync(indexName);
                _logger.LogInformation("Azure AI Search index '{IndexName}' already exists.", indexName);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogInformation("Azure AI Search index '{IndexName}' was not found. Creating it now.", indexName);
            }

            var index = new SearchIndex(indexName)
            {
                // These fields describe how Azure AI Search stores and queries product data.
                // Example:
                // - A user searches "running shoes"
                // - Azure can search inside `name` and `description`
                // - It can also filter by `brand = Nike` or sort by `price`
                Fields =
                {
                    new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true, IsSortable = true },
                    new SearchableField("name") { IsSortable = true },
                    new SearchableField("description"),
                    new SearchableField("category") { IsFilterable = true, IsFacetable = true },
                    new SearchableField("brand") { IsFilterable = true, IsFacetable = true },
                    new SimpleField("price", SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true, IsFacetable = true },
                    new SimpleField("currency", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                    new SimpleField("inStock", SearchFieldDataType.Boolean) { IsFilterable = true, IsFacetable = true },
                    new SimpleField("imageUrl", SearchFieldDataType.String),
                    new SearchableField("tags") { IsFilterable = true, IsFacetable = true },
                    new SimpleField("createdAt", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true }
                }
            };

            await indexClient.CreateIndexAsync(index);
            _logger.LogInformation("Azure AI Search index '{IndexName}' created successfully.", indexName);
        }

        public async Task SyncProductsAsync()
        {
            var searchClient = CreateProductSearchClient();
            var products = await _context.Products
                .Include(product => product.ProductBrand)
                .Include(product => product.ProductType)
                .AsNoTracking()
                .OrderBy(product => product.Id)
                .ToListAsync();

            _logger.LogInformation("Preparing to sync {ProductCount} products to Azure AI Search.", products.Count);

            // Convert every product into a search document first.
            // Example:
            // - Product: "iPhone 15"
            // - Search document: { name: "iPhone 15", brand: "Apple", category: "Phones", ... }
            var searchDocuments = products
                .Select(ToSearchDocument)
                .ToList();

            // Then split the documents into smaller batches before uploading.
            // Example with BatchSize = 500:
            // - documents 0-499   -> batch 1
            // - documents 500-999 -> batch 2
            var batches = CreateBatches(searchDocuments);

            foreach (var batch in batches)
            {
                var batchDocuments = IndexDocumentsBatch.Upload(batch);
                var result = await searchClient.IndexDocumentsAsync(batchDocuments);

                if (result.Value.Results.Any(item => !item.Succeeded))
                {
                    var failedIds = result.Value.Results
                        .Where(item => !item.Succeeded)
                        .Select(item => item.Key)
                        .ToArray();

                    throw new InvalidOperationException(
                        $"Azure AI Search indexing failed for product ids: {string.Join(", ", failedIds)}");
                }
            }

            _logger.LogInformation("Azure AI Search sync completed successfully for {ProductCount} products.", products.Count);
        }

        public async Task SyncRagDocumentsAsync()
        {
            var searchClient = CreateRagSearchClient();
            var documents = await _aiDocumentService.GetDocumentsAsync();

            // We build the text that should represent each document, then ask the embedding service
            // to convert that text into vectors for semantic/vector search.
            // Example:
            // - Text: "Delivery Information. Orders arrive in 3-5 business days"
            // - Embedding: [0.012, -0.441, 0.283, ...]
            var textsToEmbed = documents.Select(BuildRagContent).ToList();
            var embeddings = await _aiEmbeddingService.CreateEmbeddingsAsync(textsToEmbed);

            if (embeddings.Count != documents.Count)
            {
                throw new InvalidOperationException(
                    $"RAG embedding count mismatch: expected {documents.Count} embeddings but got {embeddings.Count}.");
            }

            _logger.LogInformation("Preparing to sync {DocumentCount} RAG documents to Azure AI Search.", documents.Count);

            // Build search documents while keeping each document matched to its embedding.
            // Example:
            // - documents[0] uses embeddings[0]
            // - documents[1] uses embeddings[1]
            var searchDocuments = documents
                .Select((document, index) => ToRagSearchDocument(document, embeddings[index]))
                .ToList();

            // Split the RAG documents into smaller upload batches too.
            var batches = CreateBatches(searchDocuments);

            foreach (var batch in batches)
            {
                var batchDocuments = IndexDocumentsBatch.Upload(batch);
                var result = await searchClient.IndexDocumentsAsync(batchDocuments);

                if (result.Value.Results.Any(item => !item.Succeeded))
                {
                    var failedIds = result.Value.Results
                        .Where(item => !item.Succeeded)
                        .Select(item => item.Key)
                        .ToArray();

                    throw new InvalidOperationException(
                        $"Azure AI Search RAG indexing failed for ids: {string.Join(", ", failedIds)}");
                }
            }

            _logger.LogInformation("Azure AI Search RAG sync completed successfully for {DocumentCount} documents.", documents.Count);
        }

        private SearchDocument ToSearchDocument(Product product)
        {
            var brand = product.ProductBrand?.Name;
            var category = product.ProductType?.Name;

            // This maps one Product entity into the flat shape Azure Search expects.
            // Real example:
            // - Product: "Samsung Galaxy S24", Brand: "Samsung", Type: "Phones", Price: 799
            // - Result: searchable fields users can query, filter, and sort on
            return new SearchDocument
            {
                ["id"] = product.Id.ToString(),
                ["name"] = product.Name,
                ["description"] = product.Description,
                ["category"] = category,
                ["brand"] = brand,
                ["price"] = decimal.ToDouble(product.Price),
                ["currency"] = "USD",
                ["inStock"] = true,
                ["imageUrl"] = BuildAbsolutePictureUrl(product.PictureUrl),
                ["tags"] = BuildTags(brand, category),
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O")
            };
        }

        private SearchDocument ToRagSearchDocument(AiDocument document, IReadOnlyList<double> embedding)
        {
            document.Metadata.TryGetValue("productId", out var productId);
            document.Metadata.TryGetValue("deliveryMethodId", out var deliveryMethodId);
            document.Metadata.TryGetValue("brand", out var brand);
            document.Metadata.TryGetValue("type", out var category);
            document.Metadata.TryGetValue("price", out var priceValue);
            document.Metadata.TryGetValue("url", out var url);

            var sourceId = !string.IsNullOrWhiteSpace(productId) ? productId : deliveryMethodId;

            // RAG documents keep the original text plus a vector representation.
            // Example:
            // - Title: "Return Policy"
            // - Content: "Return Policy. Items can be returned within 30 days"
            // - Vector: used to find similar meaning, even if the exact words differ
            var searchDocument = new SearchDocument
            {
                ["id"] = document.Id,
                ["sourceType"] = document.SourceType,
                ["sourceId"] = sourceId ?? document.Id,
                ["title"] = document.Title,
                ["content"] = BuildRagContent(document),
                ["brand"] = string.IsNullOrWhiteSpace(brand) ? null : brand,
                ["category"] = string.IsNullOrWhiteSpace(category) ? null : category,
                ["price"] = ParseNullableDouble(priceValue),
                ["url"] = string.IsNullOrWhiteSpace(url) ? null : url,
                ["contentVector"] = embedding.Select(value => (float)value).ToArray()
            };

            return searchDocument;
        }

        private static string BuildRagContent(AiDocument document)
        {
            // Combine title + body so embeddings/search capture both.
            // Example:
            // - Title: "Shipping"
            // - Text: "Free delivery for orders over $50"
            // - Result: "Shipping. Free delivery for orders over $50"
            return $"{document.Title}. {document.Text}".Trim();
        }

        private string BuildAbsolutePictureUrl(string pictureUrl)
        {
            if (string.IsNullOrWhiteSpace(pictureUrl))
            {
                return null;
            }

            var apiUrl = _config["ApiUrl"] ?? string.Empty;
            // Product images may be stored as relative paths in the database.
            // Example:
            // - ApiUrl = "https://localhost:5001"
            // - pictureUrl = "/images/products/shoe.png"
            // - Result = "https://localhost:5001/images/products/shoe.png"
            return $"{apiUrl}{pictureUrl}";
        }

        private static string BuildTags(params string[] values)
        {
            // Merge useful labels into a simple comma-separated string.
            // Example:
            // - brand = "Apple", category = "Laptops"
            // - Result = "Apple, Laptops"
            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Aggregate(string.Empty, (current, value) =>
                    string.IsNullOrEmpty(current) ? value : $"{current}, {value}");
        }

        private SearchClient CreateProductSearchClient()
        {
            return new SearchClient(
                new Uri(GetRequiredSetting("AzureSearch:Endpoint")),
                GetRequiredSetting("AzureSearch:IndexName"),
                new AzureKeyCredential(GetRequiredSetting("AzureSearch:ApiKey")));
        }

        private SearchClient CreateRagSearchClient()
        {
            return new SearchClient(
                new Uri(GetRequiredSetting("AzureSearch:Endpoint")),
                GetRequiredSetting("AzureSearch:RagIndexName"),
                new AzureKeyCredential(GetRequiredSetting("AzureSearch:ApiKey")));
        }

        private SearchIndexClient CreateIndexClient()
        {
            return new SearchIndexClient(
                new Uri(GetRequiredSetting("AzureSearch:Endpoint")),
                new AzureKeyCredential(GetRequiredSetting("AzureSearch:ApiKey")));
        }

        private string GetRequiredSetting(string key)
        {
            var value = _config[key];

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Missing configuration value '{key}'. Add it to appsettings, user secrets, or environment variables before using Azure AI Search sync.");
            }

            return value;
        }

        private static List<List<SearchDocument>> CreateBatches(List<SearchDocument> documents)
        {
            var batches = new List<List<SearchDocument>>();

            for (var i = 0; i < documents.Count; i += BatchSize)
            {
                var batch = documents
                    .Skip(i)
                    .Take(BatchSize)
                    .ToList();

                batches.Add(batch);
            }

            return batches;
        }

        private static double? ParseNullableDouble(string value)
        {
            // Some metadata values come in as strings, but Azure Search numeric fields need numbers.
            // Example:
            // - "49.99" -> 49.99
            // - "free" -> null
            if (double.TryParse(value, out var parsed))
            {
                return parsed;
            }

            return null;
        }
    }
}
