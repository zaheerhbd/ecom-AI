using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services
{
    public class AiDocumentService : IAiDocumentService
    {
        private readonly StoreContext _context;

        public AiDocumentService(StoreContext context)
        {
            _context = context;
        }

        public async Task<IReadOnlyList<AiDocument>> GetDocumentsAsync()
        {
            // Load the raw store data that we want the AI assistant to know about.
            var products = await _context.Products
                .Include(p => p.ProductBrand)
                .Include(p => p.ProductType)
                .OrderBy(p => p.Name)
                .ToListAsync();

            var deliveryMethods = await _context.DeliveryMethods
                .OrderBy(dm => dm.ShortName)
                .ToListAsync();

            // Convert database rows into simple text documents that are easier for AI search and retrieval.
            var documents = products.Select(BuildProductDocument)
                .Concat(deliveryMethods.Select(BuildDeliveryMethodDocument))
                .ToList();

            return documents;
        }

        private static AiDocument BuildProductDocument(Core.Entities.Product product)
        {
            return new AiDocument
            {
                Id = $"product-{product.Id}",
                SourceType = "product",
                Title = product.Name,
                // Flatten the product into one readable block of text so it can later be embedded and searched.
                Text = $"Product: {product.Name}. " +
                       $"Brand: {product.ProductBrand?.Name ?? "Unknown"}. " +
                       $"Type: {product.ProductType?.Name ?? "Unknown"}. " +
                       $"Price: ${product.Price.ToString("0.00", CultureInfo.InvariantCulture)}. " +
                       $"Description: {product.Description}",
                // Keep structured fields too, so later we can filter or link back to the real product page.
                Metadata = new Dictionary<string, string>
                {
                    ["productId"] = product.Id.ToString(CultureInfo.InvariantCulture),
                    ["brand"] = product.ProductBrand?.Name ?? string.Empty,
                    ["type"] = product.ProductType?.Name ?? string.Empty,
                    ["price"] = product.Price.ToString("0.00", CultureInfo.InvariantCulture),
                    ["url"] = $"/shop/{product.Id}"
                }
            };
        }

        private static AiDocument BuildDeliveryMethodDocument(Core.Entities.OrderAggregate.DeliveryMethod deliveryMethod)
        {
            return new AiDocument
            {
                Id = $"delivery-{deliveryMethod.Id}",
                SourceType = "policy",
                Title = $"{deliveryMethod.ShortName} delivery",
                // Treat shipping details like support knowledge so the assistant can answer policy-style questions.
                Text = $"Delivery option: {deliveryMethod.ShortName}. " +
                       $"Estimated delivery time: {deliveryMethod.DeliveryTime}. " +
                       $"Cost: ${deliveryMethod.Price.ToString("0.00", CultureInfo.InvariantCulture)}. " +
                       $"Details: {deliveryMethod.Description}",
                Metadata = new Dictionary<string, string>
                {
                    ["deliveryMethodId"] = deliveryMethod.Id.ToString(CultureInfo.InvariantCulture),
                    ["shortName"] = deliveryMethod.ShortName ?? string.Empty,
                    ["deliveryTime"] = deliveryMethod.DeliveryTime ?? string.Empty,
                    ["price"] = deliveryMethod.Price.ToString("0.00", CultureInfo.InvariantCulture)
                }
            };
        }
    }
}
