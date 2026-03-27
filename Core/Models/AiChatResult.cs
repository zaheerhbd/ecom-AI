using System.Collections.Generic;

namespace Core.Models
{
    public class AiChatResult
    {
        public string Answer { get; set; }
        public IReadOnlyList<AiDocument> Sources { get; set; } = new List<AiDocument>();
        public IReadOnlyList<string> FollowUpSuggestions { get; set; } = new List<string>();
    }
}
