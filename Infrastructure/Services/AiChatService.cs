using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;

namespace Infrastructure.Services
{
    public class AiChatService : IAiChatService
    {
        // Common filler words are ignored so matching focuses on the useful parts of the question.
        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "are", "best", "by", "do", "for", "have", "i", "in", "is", "me",
            "my", "of", "on", "or", "show", "the", "to", "what", "with", "you"
        };

        private readonly IAiDocumentService _aiDocumentService;

        public AiChatService(IAiDocumentService aiDocumentService)
        {
            _aiDocumentService = aiDocumentService;
        }

        public async Task<AiChatResult> AskAsync(string question)
        {
            // Handle empty questions early so the rest of the method can assume there is something to search for.
            if (string.IsNullOrWhiteSpace(question))
            {
                return new AiChatResult
                {
                    Answer = "Please ask a shopping or delivery question so I can help."
                };
            }

            // Load the AI-ready catalog and delivery documents that were prepared by AiDocumentService.
            var documents = await _aiDocumentService.GetDocumentsAsync();
            var keywords = ExtractKeywords(question);

            // Score each document by how many important keywords it contains, then keep the best few matches.
            var matches = documents
                .Select(document => new
                {
                    Document = document,
                    Score = CalculateScore(document, keywords)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Document.Title)
                .Take(3)
                .Select(x => x.Document)
                .ToList();

            // Return a helpful fallback when the simple keyword matcher cannot find anything relevant.
            if (!matches.Any())
            {
                return new AiChatResult
                {
                    Answer = "I could not find a strong match yet. Try asking about a brand, product type, budget, or delivery option.",
                    FollowUpSuggestions = new List<string>
                    {
                        "Show me Nike products",
                        "What delivery options do you have?",
                        "Find affordable products under $100"
                    }
                };
            }

            // Build a plain response for now; later this can be replaced by an LLM prompt using retrieved context.
            var answer = BuildAnswer(matches);

            return new AiChatResult
            {
                Answer = answer,
                Sources = matches,
                FollowUpSuggestions = BuildFollowUps(matches)
            };
        }

        private static IReadOnlyList<string> ExtractKeywords(string question)
        {
            // Break the question into lowercase words, remove filler terms, and keep distinct search keywords.
            return Regex.Matches(question.ToLowerInvariant(), "[a-z0-9]+")
                .Select(match => match.Value)
                .Where(word => !StopWords.Contains(word))
                .Distinct()
                .ToList();
        }

        private static int CalculateScore(AiDocument document, IReadOnlyList<string> keywords)
        {
            if (!keywords.Any())
            {
                return 0;
            }

            // Search across the title, main text, and metadata so brand/type filters can still influence the result.
            var haystack = $"{document.Title} {document.Text} {string.Join(" ", document.Metadata.Values)}"
                .ToLowerInvariant();

            return keywords.Count(keyword => haystack.Contains(keyword));
        }

        private static string BuildAnswer(IReadOnlyList<AiDocument> matches)
        {
            var topMatch = matches[0];

            // Delivery matches are phrased like support answers, while product matches read like recommendations.
            if (topMatch.SourceType == "policy")
            {
                return $"I found delivery information that looks relevant. {topMatch.Text}";
            }

            var titles = string.Join(", ", matches.Select(match => match.Title));

            return $"I found these relevant products: {titles}. The top match is {topMatch.Text}";
        }

        private static IReadOnlyList<string> BuildFollowUps(IReadOnlyList<AiDocument> matches)
        {
            // Offer next questions based on whether the user seems to be asking about policies or products.
            if (matches.Any(match => match.SourceType == "policy"))
            {
                return new List<string>
                {
                    "Which delivery option is cheapest?",
                    "Which delivery option is fastest?"
                };
            }

            return new List<string>
            {
                "Show me more like this",
                "Which option is the cheapest?",
                "What delivery options are available?"
            };
        }
    }
}
