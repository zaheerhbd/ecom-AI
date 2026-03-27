using API.Dtos;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers
{
    public class AiController : BaseApiController
    {
        private readonly IAiChatService _aiChatService;
        private readonly IAiDocumentService _aiDocumentService;

        public AiController(IAiDocumentService aiDocumentService, IAiChatService aiChatService)
        {
            _aiDocumentService = aiDocumentService;
            _aiChatService = aiChatService;
        }

        [HttpGet("documents")]
        public async Task<ActionResult<IReadOnlyList<AiDocument>>> GetDocuments()
        {
            var documents = await _aiDocumentService.GetDocumentsAsync();

            return Ok(documents);
        }

        [HttpPost("chat")]
        public async Task<ActionResult<AiChatResponseDto>> Chat(AiChatRequestDto request)
        {
            var result = await _aiChatService.AskAsync(request?.Question);

            var response = new AiChatResponseDto
            {
                Answer = result.Answer,
                Sources = result.Sources.Select(source => new AiChatSourceDto
                {
                    Id = source.Id,
                    SourceType = source.SourceType,
                    Title = source.Title,
                    Text = source.Text,
                    Metadata = source.Metadata
                }).ToList(),
                FollowUpSuggestions = result.FollowUpSuggestions
            };

            return Ok(response);
        }
    }
}
