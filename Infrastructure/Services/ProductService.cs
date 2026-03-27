using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Entities;
using Core.Interfaces;
using Core.Specifications;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services
{
    public class ProductService : IProductService
    {
        private readonly StoreContext _context;

        public ProductService(StoreContext context)
        {
            _context = context;
        }

        public async Task<(IReadOnlyList<Product> Products, int TotalItems)> GetProductsAsync(ProductSpecParams productParams)
        {
            var query = _context.Products
                .Include(p => p.ProductBrand)
                .Include(p => p.ProductType)
                .AsQueryable();

            query = ApplyFilters(query, productParams);

            var totalItems = await query.CountAsync();

            query = ApplySorting(query, productParams);

            var products = await query
                .Skip(productParams.PageSize * (productParams.PageIndex - 1))
                .Take(productParams.PageSize)
                .ToListAsync();

            return (products, totalItems);
        }

        public async Task<Product> GetProductByIdAsync(int id)
        {
            return await _context.Products
                .Include(p => p.ProductBrand)
                .Include(p => p.ProductType)
                .FirstOrDefaultAsync(p => p.Id == id);
        }

        public async Task<IReadOnlyList<ProductBrand>> GetBrandsAsync()
        {
            return await _context.ProductBrands
                .OrderBy(b => b.Name)
                .ToListAsync();
        }

        public async Task<IReadOnlyList<ProductType>> GetTypesAsync()
        {
            return await _context.ProductTypes
                .OrderBy(t => t.Name)
                .ToListAsync();
        }

        private static IQueryable<Product> ApplyFilters(IQueryable<Product> query, ProductSpecParams productParams)
        {
            if (!string.IsNullOrWhiteSpace(productParams.Search))
            {
                query = query.Where(p => p.Name.ToLower().Contains(productParams.Search));
            }

            if (productParams.BrandId.HasValue)
            {
                query = query.Where(p => p.ProductBrandId == productParams.BrandId.Value);
            }

            if (productParams.TypeId.HasValue)
            {
                query = query.Where(p => p.ProductTypeId == productParams.TypeId.Value);
            }

            return query;
        }

        private static IQueryable<Product> ApplySorting(IQueryable<Product> query, ProductSpecParams productParams)
        {
            return productParams.Sort switch
            {
                "priceAsc" => query.OrderBy(p => p.Price),
                "priceDesc" => query.OrderByDescending(p => p.Price),
                _ => query.OrderBy(p => p.Name)
            };
        }
    }
}
