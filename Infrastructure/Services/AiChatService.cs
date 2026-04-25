using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services
{
    public class AiChatService : IAiChatService
    {
        private const double MinimumConfidenceScore = 0.59;
        private const double StructuredFilterMinimumConfidenceScore = 0.55;
        private const string ProductSourceType = "product";
        private const string PolicySourceType = "policy";

        private readonly IAiRetrieverService _aiRetrieverService;
        private readonly IAiAnswerGeneratorService _aiAnswerGeneratorService;
        private readonly ILogger<AiChatService> _logger;

        public AiChatService(
            IAiRetrieverService aiRetrieverService,
            IAiAnswerGeneratorService aiAnswerGeneratorService,
            ILogger<AiChatService> logger)
        {
            _aiRetrieverService = aiRetrieverService;
            _aiAnswerGeneratorService = aiAnswerGeneratorService;
            _logger = logger;
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

            var preferredSourceType = DetectPreferredSourceType(question);
            var maximumPrice = DetectMaximumPrice(question);

            _logger.LogInformation(
                "AI chat intent for question \"{Question}\" => preferredSourceType={PreferredSourceType}, maximumPrice={MaximumPrice}",
                question,
                preferredSourceType ?? "none",
                maximumPrice?.ToString("0.##") ?? "none");

            var retrievedMatches = (await _aiRetrieverService.SearchAsync(question, 5, preferredSourceType, maximumPrice))
                .ToList();

            if (!retrievedMatches.Any())
            {
                _logger.LogInformation(
                    "AI chat found no matches for question \"{Question}\".",
                    question);

                return new AiChatResult
                {
                    Answer = "I could not find a strong match yet. Try asking about a product, brand, price, or delivery option.",
                    FollowUpSuggestions = BuildDefaultFollowUps()
                };
            }

            var topScore = retrievedMatches[0].Score;
            var topTitles = string.Join(", ", retrievedMatches.Select(match => match.Chunk.Title));
            var topScores = string.Join(", ", retrievedMatches.Select(match => match.Score.ToString("0.000")));
            var minimumRequiredScore = maximumPrice.HasValue
                ? StructuredFilterMinimumConfidenceScore
                : MinimumConfidenceScore;

            _logger.LogInformation(
                "AI chat retrieval for question \"{Question}\" => topScore={TopScore}, scores=[{Scores}], titles=[{Titles}]",
                question,
                topScore,
                topScores,
                topTitles);

            if (topScore < minimumRequiredScore)
            {
                _logger.LogInformation(
                    "AI chat rejected low-confidence answer for question \"{Question}\" because topScore={TopScore} is below threshold={Threshold}.",
                    question,
                    topScore,
                    minimumRequiredScore);

                return new AiChatResult
                {
                    Answer = "I found some related information, but not enough to answer confidently. Try asking in a more specific way.",
                    FollowUpSuggestions = BuildDefaultFollowUps()
                };
            }

            var matches = ApplyStructuredOrdering(question, retrievedMatches)
                .Take(3)
                .ToList();

            if (!HasSourceCoverage(question, matches))
            {
                _logger.LogInformation(
                    "AI chat rejected answer for question \"{Question}\" because the retrieved sources do not explicitly cover the requested topic.",
                    question);

                return new AiChatResult
                {
                    Answer = "I found related information, but I could not find a source that clearly confirms this. Try asking about a product, delivery option, price, or brand that is listed in the store.",
                    FollowUpSuggestions = BuildDefaultFollowUps()
                };
            }

            var topChunks = matches
                .Select(match => match.Chunk)
                .ToList();
            var fallbackAnswer = BuildAnswer(topChunks);
            var generatedAnswer = await _aiAnswerGeneratorService.GenerateAnswerAsync(question, topChunks, fallbackAnswer);

            return new AiChatResult
            {
                Answer = generatedAnswer,
                Sources = topChunks.Select(ToSourceDocument).ToList(),
                FollowUpSuggestions = BuildFollowUps(topChunks)
            };
        }

        private static string DetectPreferredSourceType(string question)
        {
            var normalizedQuestion = question.ToLowerInvariant();

            if (ContainsAny(normalizedQuestion, "delivery", "shipping", "ship", "shipped"))
            {
                return PolicySourceType;
            }

            if (ContainsAny(
                normalizedQuestion,
                "product",
                "products",
                "hat",
                "hats",
                "glove",
                "gloves",
                "boot",
                "boots",
                "board",
                "boards",
                "react",
                "netcore",
                "angular",
                "under",
                "cheapest",
                "expensive"))
            {
                return ProductSourceType;
            }

            return null;
        }

        private static IReadOnlyList<AiSearchMatch> ApplyStructuredOrdering(
            string question,
            IReadOnlyList<AiSearchMatch> matches)
        {
            var normalizedQuestion = question.ToLowerInvariant();

            if (ContainsAny(normalizedQuestion, "cheapest", "lowest price", "least expensive"))
            {
                return matches
                    .OrderBy(match => TryGetMetadataDouble(match.Chunk, "price", out var price) ? price : double.MaxValue)
                    .ThenByDescending(match => match.Score)
                    .ThenBy(match => match.Chunk.Title)
                    .ToList();
            }

            if (ContainsAny(normalizedQuestion, "most expensive", "highest price"))
            {
                return matches
                    .OrderByDescending(match => TryGetMetadataDouble(match.Chunk, "price", out var price) ? price : double.MinValue)
                    .ThenByDescending(match => match.Score)
                    .ThenBy(match => match.Chunk.Title)
                    .ToList();
            }

            return matches;
        }

        private static double? DetectMaximumPrice(string question)
        {
            var match = Regex.Match(
                question,
                @"\b(?:under|below|less than|cheaper than)\s*\$?\s*(\d+(?:\.\d+)?)",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                return null;
            }

            if (double.TryParse(match.Groups[1].Value, out var maximumPrice))
            {
                return maximumPrice;
            }

            return null;
        }

        private bool HasSourceCoverage(string question, IReadOnlyList<AiSearchMatch> matches)
        {
            var normalizedQuestion = question.ToLowerInvariant();
            var sourceText = BuildCombinedSourceText(matches);

            if (ContainsAny(normalizedQuestion, "cash on delivery", "cod"))
            {
                return ContainsAny(sourceText, "cash on delivery", "cod", "cash", "payment");
            }

            if (ContainsAny(normalizedQuestion, "discount", "discounts", "coupon", "coupons", "sale", "promotion"))
            {
                return ContainsAny(sourceText, "discount", "coupon", "sale", "promotion");
            }

            if (ContainsAny(normalizedQuestion, "next day", "overnight"))
            {
                return ContainsAny(sourceText, "next day", "overnight");
            }

            if (ContainsAny(normalizedQuestion, "international", "worldwide"))
            {
                return ContainsAny(sourceText, "international", "worldwide", "countries", "country");
            }

            if (ContainsAny(normalizedQuestion, "return", "refund"))
            {
                return ContainsAny(sourceText, "return", "refund");
            }

            if (ContainsAny(normalizedQuestion, "warranty", "guarantee"))
            {
                return ContainsAny(sourceText, "warranty", "guarantee");
            }

            if (ContainsAny(normalizedQuestion, "crypto", "cryptocurrency", "bitcoin"))
            {
                return ContainsAny(sourceText, "crypto", "cryptocurrency", "bitcoin");
            }

            if (ContainsAny(normalizedQuestion, "store", "stores", "location", "locations", "new york"))
            {
                return ContainsAny(sourceText, "store", "stores", "location", "locations", "new york");
            }

            return true;
        }

        private static string BuildCombinedSourceText(IReadOnlyList<AiSearchMatch> matches)
        {
            var sourceParts = matches.Select(match =>
                $"{match.Chunk.Title} {match.Chunk.Text} {string.Join(" ", match.Chunk.Metadata.Values)}");

            return string.Join(" ", sourceParts).ToLowerInvariant();
        }

        private static bool TryGetMetadataDouble(AiDocumentChunk chunk, string key, out double value)
        {
            value = 0;

            return chunk.Metadata.TryGetValue(key, out var rawValue)
                && double.TryParse(rawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static bool ContainsAny(string value, params string[] keywords)
        {
            return keywords.Any(value.Contains);
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
