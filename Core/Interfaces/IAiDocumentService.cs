using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Models;

namespace Core.Interfaces
{
    public interface IAiDocumentService
    {
        Task<IReadOnlyList<AiDocument>> GetDocumentsAsync();
    }
}
