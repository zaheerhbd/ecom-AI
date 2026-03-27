using System.Collections.Generic;

namespace API.Dtos
{
    public class AiChatSourceDto
    {
        public string Id { get; set; }
        public string SourceType { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }
}
