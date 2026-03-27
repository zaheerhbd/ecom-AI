using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Entities;
using Core.Specifications;

namespace Core.Interfaces
{
    public interface IProductService
    {
        Task<(IReadOnlyList<Product> Products, int TotalItems)> GetProductsAsync(ProductSpecParams productParams);
        Task<Product> GetProductByIdAsync(int id);
        Task<IReadOnlyList<ProductBrand>> GetBrandsAsync();
        Task<IReadOnlyList<ProductType>> GetTypesAsync();
    }
}
