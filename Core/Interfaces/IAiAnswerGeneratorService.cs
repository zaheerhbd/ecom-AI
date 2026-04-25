using System.Collections.Generic;
using System.Threading.Tasks;
using Core.Models;

namespace Core.Interfaces
{
    public interface IAiAnswerGeneratorService
    {
        Task<string> GenerateAnswerAsync(
            string question,
            IReadOnlyList<AiDocumentChunk> sources,
            string fallbackAnswer);
    }
}
