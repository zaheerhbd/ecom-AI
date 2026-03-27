using System.Collections.Generic;

namespace API.Dtos
{
    public class AiChatResponseDto
    {
        public string Answer { get; set; }
        public IReadOnlyList<AiChatSourceDto> Sources { get; set; } = new List<AiChatSourceDto>();
        public IReadOnlyList<string> FollowUpSuggestions { get; set; } = new List<string>();
    }
}
