using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;

namespace Infrastructure.Services
{
    public class AiChatService : IAiChatService
    {
        private readonly IAiRetrieverService _aiRetrieverService;

        public AiChatService(IAiRetrieverService aiRetrieverService)
        {
            _aiRetrieverService = aiRetrieverService;
        }

        public async Task<AiChatResult> AskAsync(string question)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                return new AiChatResult
                {
                    Answer = "Please ask a shopping or delivery question so I can help."
                };
            }

            var matches = (await _aiRetrieverService.SearchAsync(question, 5))
                .Select(match => match.Chunk)
                .Take(3)
                .ToList();

            if (!matches.Any())
            {
                return new AiChatResult
                {
                    Answer = "I could not find a strong match yet. Try asking about a product, brand, price, or delivery option.",
                    FollowUpSuggestions = BuildDefaultFollowUps()
                };
            }

            return new AiChatResult
            {
                Answer = BuildAnswer(matches),
                Sources = matches.Select(ToSourceDocument).ToList(),
                FollowUpSuggestions = BuildFollowUps(matches)
            };
        }

        private static AiDocument ToSourceDocument(AiDocumentChunk chunk)
        {
            return new AiDocument
            {
                Id = chunk.DocumentId,
                SourceType = chunk.SourceType,
                Title = chunk.Title,
                Text = chunk.Text,
                Metadata = new Dictionary<string, string>(chunk.Metadata)
            };
        }

        private static string BuildAnswer(IReadOnlyList<AiDocumentChunk> matches)
        {
            var topMatch = matches[0];
            var titles = string.Join(", ", matches.Select(match => match.Title));

            if (matches.All(match => string.Equals(match.SourceType, "policy", StringComparison.OrdinalIgnoreCase)))
            {
                return $"I found these relevant delivery options: {titles}. The top match is {topMatch.Text}";
            }

            return $"I found these relevant results: {titles}. The top match is {topMatch.Text}";
        }

        private static IReadOnlyList<string> BuildFollowUps(IReadOnlyList<AiDocumentChunk> matches)
        {
            if (matches.All(match => string.Equals(match.SourceType, "policy", StringComparison.OrdinalIgnoreCase)))
            {
                return new List<string>
                {
                    "Which delivery option is cheapest?",
                    "Which delivery option is fastest?",
                    "Do you have free delivery?"
                };
            }

            return BuildDefaultFollowUps();
        }

        private static IReadOnlyList<string> BuildDefaultFollowUps()
        {
            return new List<string>
            {
                "Show me hats",
                "Show me React products",
                "What delivery options are available?"
            };
        }
    }
}
