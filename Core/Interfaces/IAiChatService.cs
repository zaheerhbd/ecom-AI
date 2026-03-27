using System.Threading.Tasks;
using Core.Models;

namespace Core.Interfaces
{
    public interface IAiChatService
    {
        Task<AiChatResult> AskAsync(string question);
    }
}
