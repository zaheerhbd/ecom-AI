using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;

namespace Infrastructure.Services
{
    public class AiChatService : IAiChatService
    {
        // These are common words that do not help much with search.
        // Example: in "show me hats under 20", words like "show" and "me" are ignored.
        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "are", "best", "by", "can", "do", "find", "for", "have", "i", "in",
            "is", "me", "my", "of", "on", "or", "please", "show", "the", "to", "what", "with", "you"
        };

        // If the question contains one of these words, we treat it like a delivery/shipping question.
        // Example: "what is the cheapest delivery option?"
        private static readonly string[] DeliveryTerms =
        {
            "delivery", "deliver", "deliverytime", "shipping", "ship", "shippingcost", "postage", "arrive"
        };

        // These words help us recognize product-shopping questions.
        // Example: "show me boards under 100"
        private static readonly string[] ProductTerms =
        {
            "product", "products", "item", "items", "board", "boards", "hat", "hats", "boot", "boots", "glove", "gloves"
        };

        private readonly IAiRetrieverService _aiRetrieverService;

        public AiChatService(IAiRetrieverService aiRetrieverService)
        {
            _aiRetrieverService = aiRetrieverService;
        }

        public async Task<AiChatResult> AskAsync(string question)
        {
            // Step 1: stop early if the user did not ask anything useful.
            if (string.IsNullOrWhiteSpace(question))
            {
                return new AiChatResult
                {
                    Answer = "Please ask a shopping or delivery question so I can help."
                };
            }

            // Step 2: understand the question.
            // Example: "show me hats under 20" becomes a product query with max price = 20.
            // Example shape of "intent":
            // {
            //   Keywords = ["hats", "20"],
            //   WantsDelivery = false,
            //   WantsProducts = true,
            //   WantsAffordable = true,
            //   MaxPrice = 20
            // }
            var intent = ParseIntent(question);

            // Step 3: retrieve the most relevant chunks, then rerank them with business-aware intent rules.
            // Easy idea:
            // - retrieval finds text that looks semantically close to the question
            // - reranking applies shop rules like budget, delivery speed, and cheapest option
            // Example shape coming back from SearchAsync(question, 10):
            // [
            //   { Chunk = { Title = "Green React Woolen Hat", Text = "Type: Hat." }, Score = 0.81 },
            //   { Chunk = { Title = "Green React Woolen Hat", Text = "Price: $8." }, Score = 0.76 }
            // ]
            var matches = (await _aiRetrieverService.SearchAsync(question, 10))
                .Select(match => new
                {
                    Match = match,
                    // We combine two scores:
                    // 1. retriever score = "does this chunk look similar to the question?"
                    // 2. intent score = "does this chunk fit the shopping rules from the question?"
                    // Example:
                    // retriever score = 0.81
                    // intent score = 10
                    // final combined score = 10.81
                    Score = match.Score + CalculateScore(match.Chunk, intent)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Match.Chunk.Title)
                // Group by original document so we do not return many chunks from the same product.
                // Example:
                // If 3 chunks all belong to "product-8", keep only the best one from that product.
                .GroupBy(x => x.Match.Chunk.DocumentId)
                .Select(group => group.First().Match.Chunk)
                .Take(3)
                .ToList();

            // Step 4: if nothing matched well, return a helpful fallback instead of a vague answer.
            if (!matches.Any())
            {
                return new AiChatResult
                {
                    Answer = BuildNoMatchAnswer(intent),
                    FollowUpSuggestions = BuildNoMatchFollowUps(intent)
                };
            }

            // Step 5: build the final answer, include the matched sources, and suggest follow-up questions.
            // Example shape of final API result:
            // {
            //   Answer = "I found affordable options that look relevant: Green React Woolen Hat...",
            //   Sources = [ ...matched source documents... ],
            //   FollowUpSuggestions = [ "Show me more products under $100", ... ]
            // }
            return new AiChatResult
            {
                Answer = BuildAnswer(matches, intent),
                Sources = matches.Select(ToSourceDocument).ToList(),
                FollowUpSuggestions = BuildFollowUps(matches, intent)
            };
        }

        private static QueryIntent ParseIntent(string question)
        {
            var normalized = question.ToLowerInvariant();

            // Example:
            // question   = "show me hats under 20"
            // normalized = "show me hats under 20"
            var keywords = ExtractKeywords(question);

            // Detect whether the user is asking about delivery or shipping.
            // Example: "what delivery options do you have?"
            var wantsDelivery = DeliveryTerms.Any(term => normalized.Contains(term));

            // Detect common comparison words.
            // Example: "fastest delivery" or "cheapest option"
            var wantsFastest = normalized.Contains("fastest") || normalized.Contains("quickest") || normalized.Contains("fast");
            var wantsCheapest = normalized.Contains("cheapest") || normalized.Contains("cheaper")
                || normalized.Contains("lowest") || normalized.Contains("least") || normalized.Contains("free");

            // Detect budget-style shopping questions.
            // Example: "show me affordable hats"
            // Example: "find cheap products under 100"
            var wantsAffordable = wantsCheapest || normalized.Contains("affordable") || normalized.Contains("budget")
                || normalized.Contains("cheap") || normalized.Contains("under") || normalized.Contains("below");

            // Try to pull price limits from the sentence.
            // Example: "under 100" -> MaxPrice = 100
            // Example: "more than 50" -> MinPrice = 50
            // Regex note:
            // (?:...) means "group these words together, but do not store the group as the result"
            // \\s* means "allow zero or more spaces"
            // \\$? means "an optional dollar sign"
            // (\\d+(?:\\.\\d{1,2})?) means "a number like 20 or 20.50"
            var maxPrice = ExtractPrice(normalized, "(?:under|below|less than|up to|max(?:imum)?|cheaper than)\\s*\\$?\\s*(\\d+(?:\\.\\d{1,2})?)");
            var minPrice = ExtractPrice(normalized, "(?:over|above|more than|at least|min(?:imum)?|starting at)\\s*\\$?\\s*(\\d+(?:\\.\\d{1,2})?)");

            // If the user asked about delivery only, we can ignore products.
            // Example: "what is the cheapest delivery?" -> WantsProducts = false
            // Example: "show me products with fast delivery" -> WantsProducts = true
            var wantsProducts = !wantsDelivery || ProductTerms.Any(term => normalized.Contains(term));

            return new QueryIntent
            {
                Keywords = keywords,
                WantsDelivery = wantsDelivery,
                WantsProducts = wantsProducts,
                WantsCheapest = wantsCheapest,
                WantsFastest = wantsFastest,
                WantsAffordable = wantsAffordable,
                MaxPrice = maxPrice,
                MinPrice = minPrice
            };
        }

        private static IReadOnlyList<string> ExtractKeywords(string question)
        {
            // Break the question into simple words and remove filler words.
            // Example: "show me React boards" -> "react", "boards"
            // Regex note:
            // [a-z0-9]+ means "take one or more letters or digits"
            // This helps us split a sentence into search words.
            // Example input:
            // "show me hats under 20"
            // Example output:
            // ["hats", "under", "20"]
            return Regex.Matches(question.ToLowerInvariant(), "[a-z0-9]+")
                .Select(match => match.Value)
                .Where(word => !StopWords.Contains(word))
                .Distinct()
                .ToList();
        }

        private static decimal? ExtractPrice(string input, string pattern)
        {
            // Try to pull a number from phrases like "under 100" or "more than 50".
            // Example: "show me hats under 20" returns 20.
            // The regex pattern tells us:
            // 1. which words can appear before the price, such as "under" or "above"
            // 2. that spaces are optional
            // 3. that the dollar sign is optional
            // 4. and that the final captured part should be the number itself
            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            return decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
                ? price
                : (decimal?)null;
        }

        private static double CalculateScore(AiDocumentChunk document, QueryIntent intent)
        {
            // Send delivery chunks to delivery scoring and product chunks to product scoring.
            // Example:
            // if SourceType = "policy", use delivery rules
            // if SourceType = "product", use product rules
            return document.SourceType == "policy"
                ? CalculateDeliveryScore(document, intent)
                : CalculateProductScore(document, intent);
        }

        private static double CalculateDeliveryScore(AiDocumentChunk document, QueryIntent intent)
        {
            // Ignore delivery chunks if the user did not ask a delivery question.
            if (!intent.WantsDelivery)
            {
                return 0;
            }

            // Start with keyword matching, then boost based on price or speed.
            // Example "document":
            // {
            //   Title = "FREE delivery",
            //   Text = "Cost: $0. Estimated delivery time: 1-2 Weeks."
            // }
            var score = KeywordMatches(document, intent.Keywords) * 2 + 4;
            var price = GetMetadataDecimal(document, "price") ?? 0m;
            var deliveryDays = EstimateDeliveryDays(document.Metadata.TryGetValue("deliveryTime", out var deliveryTime) ? deliveryTime : document.Text);

            // Example: if user asks for the cheapest delivery, lower price should rank higher.
            if (intent.WantsCheapest || intent.WantsAffordable)
            {
                score += 12 - (int)Math.Min(price, 10m);
            }

            // Example: if user asks for the fastest delivery, fewer days should rank higher.
            if (intent.WantsFastest)
            {
                score += Math.Max(0, 12 - deliveryDays);
            }

            return score;
        }

        private static double CalculateProductScore(AiDocumentChunk document, QueryIntent intent)
        {
            // If the question is only about delivery, do not rank product chunks.
            if (intent.WantsDelivery && !intent.WantsProducts)
            {
                return 0;
            }

            // Example "document":
            // {
            //   Title = "Green React Woolen Hat",
            //   Text = "Price: $8.",
            //   Metadata = { brand = "React", type = "Hat", price = "8.00" }
            // }
            var price = GetMetadataDecimal(document, "price");

            // Filter out products outside the requested price range.
            // Example: "under 100" removes products priced above 100.
            if (intent.MaxPrice.HasValue && (!price.HasValue || price.Value > intent.MaxPrice.Value))
            {
                return 0;
            }

            if (intent.MinPrice.HasValue && (!price.HasValue || price.Value < intent.MinPrice.Value))
            {
                return 0;
            }

            // Start with keyword matches, then add extra score for matching brand/type.
            var score = KeywordMatches(document, intent.Keywords) * 3;

            if (document.Metadata.TryGetValue("brand", out var brand) && intent.Keywords.Contains(brand.ToLowerInvariant()))
            {
                score += 4;
            }

            if (document.Metadata.TryGetValue("type", out var type) && intent.Keywords.Contains(type.ToLowerInvariant()))
            {
                score += 4;
            }

            if (intent.MaxPrice.HasValue && price.HasValue)
            {
                score += 6;
            }

            // Example: if the question says "affordable", cheaper products get a better score.
            if ((intent.WantsAffordable || intent.WantsCheapest) && price.HasValue)
            {
                score += Math.Max(1, 12 - (int)Math.Min(price.Value / 10m, 11m));
            }

            return score;
        }

        private static int KeywordMatches(AiDocumentChunk document, IReadOnlyList<string> keywords)
        {
            if (!keywords.Any())
            {
                return 0;
            }

            // Search in the title, text, and metadata so "React", "hat", or "price" can all help ranking.
            // Example:
            // keywords = ["hat", "20"]
            // haystack = "green react woolen hat type: hat react hat 8.00 /shop/8"
            var haystack = $"{document.Title} {document.Text} {string.Join(" ", document.Metadata.Values)}".ToLowerInvariant();
            return keywords.Count(keyword => haystack.Contains(keyword));
        }

        private static decimal? GetMetadataDecimal(AiDocumentChunk document, string key)
        {
            // Read numeric values stored as text in document metadata.
            // Example: a product chunk may store price as "18.00", which we convert back to 18.00.
            if (!document.Metadata.TryGetValue(key, out var rawValue))
            {
                return null;
            }

            return decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : (decimal?)null;
        }

        private static int EstimateDeliveryDays(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                // If we do not know the delivery time, use a slower default so it does not win "fastest" searches.
                return 14;
            }

            // Convert text like "1-2 Days" or "1-2 Weeks" into a rough number we can compare.
            // Regex note:
            // \\d+ means "find one or more digits"
            // Example: from "1-2 Days" we capture 1 and 2, then use the smallest value as the fastest estimate.
            var lower = value.ToLowerInvariant();
            var matches = Regex.Matches(lower, "\\d+").Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture)).ToList();

            if (lower.Contains("week") && matches.Any())
            {
                return matches.Min() * 7;
            }

            return matches.Any() ? matches.Min() : 14;
        }

        private static AiDocument ToSourceDocument(AiDocumentChunk chunk)
        {
            // Convert a chunk back into the source shape the API already returns to the frontend.
            // Example input:
            // { DocumentId = "product-8", Title = "Green React Woolen Hat", Text = "Price: $8." }
            // Example output:
            // { Id = "product-8", Title = "Green React Woolen Hat", Text = "Price: $8." }
            return new AiDocument
            {
                Id = chunk.DocumentId,
                SourceType = chunk.SourceType,
                Title = chunk.Title,
                Text = chunk.Text,
                Metadata = new Dictionary<string, string>(chunk.Metadata)
            };
        }

        private static string BuildAnswer(IReadOnlyList<AiDocumentChunk> matches, QueryIntent intent)
        {
            // Example "matches":
            // [
            //   { Title = "Green React Woolen Hat", Text = "Price: $8." },
            //   { Title = "Purple React Woolen Hat", Text = "Price: $15." }
            // ]
            var topMatch = matches[0];

            // Delivery questions get delivery-style answers.
            if (topMatch.SourceType == "policy")
            {
                if (intent.WantsCheapest || intent.WantsAffordable)
                {
                    return $"The cheapest delivery option I found is {topMatch.Title} at ${GetMetadataDecimal(topMatch, "price"):0.00}. {topMatch.Text}";
                }

                if (intent.WantsFastest)
                {
                    return $"The fastest delivery option I found is {topMatch.Title}. {topMatch.Text}";
                }

                var deliveryTitles = string.Join(", ", matches.Select(match => match.Title));
                return $"I found these relevant delivery options: {deliveryTitles}. The best match is {topMatch.Text}";
            }

            var productSummary = string.Join(", ", matches.Select(match => match.Title));

            // Product questions get product-style answers.
            if (intent.MaxPrice.HasValue)
            {
                return $"I found products at or below ${intent.MaxPrice.Value:0.00}: {productSummary}. The top match is {topMatch.Text}";
            }

            if (intent.WantsAffordable || intent.WantsCheapest)
            {
                return $"I found affordable options that look relevant: {productSummary}. The best match is {topMatch.Text}";
            }

            return $"I found these relevant products: {productSummary}. The top match is {topMatch.Text}";
        }

        private static string BuildNoMatchAnswer(QueryIntent intent)
        {
            // Return a more helpful fallback based on the type of question.
            if (intent.WantsDelivery)
            {
                return "I could not find a strong delivery match yet. Try asking which option is cheapest, fastest, or free.";
            }

            if (intent.MaxPrice.HasValue)
            {
                return $"I could not find a strong product match under ${intent.MaxPrice.Value:0.00} yet. Try another brand, product type, or a higher budget.";
            }

            return "I could not find a strong match yet. Try asking about a brand, product type, budget, or delivery option.";
        }

        private static IReadOnlyList<string> BuildNoMatchFollowUps(QueryIntent intent)
        {
            // Even when nothing matches, suggest a few next questions to guide the user.
            if (intent.WantsDelivery)
            {
                return new List<string>
                {
                    "Which delivery option is cheapest?",
                    "Which delivery option is fastest?",
                    "Do you have free delivery?"
                };
            }

            return new List<string>
            {
                "Show me React boards",
                "Find affordable products under $100",
                "Show me hats"
            };
        }

        private static IReadOnlyList<string> BuildFollowUps(IReadOnlyList<AiDocumentChunk> matches, QueryIntent intent)
        {
            // Follow-up buttons depend on whether the matched results are delivery-related or product-related.
            // Example output:
            // [ "Show me more products under $100", "Which option is the cheapest?" ]
            if (matches.Any(match => match.SourceType == "policy"))
            {
                return new List<string>
                {
                    "Which delivery option is cheapest?",
                    "Which delivery option is fastest?",
                    "Do you have free delivery?"
                };
            }

            if (intent.MaxPrice.HasValue || intent.WantsAffordable)
            {
                return new List<string>
                {
                    "Show me more products under $100",
                    "Which option is the cheapest?",
                    "What delivery options are available?"
                };
            }

            return new List<string>
            {
                "Show me more like this",
                "Find affordable products under $100",
                "What delivery options are available?"
            };
        }

        private sealed class QueryIntent
        {
            // This small class stores what we understood from the user's question.
            // Example: "show me hats under 20" means WantsProducts = true and MaxPrice = 20.
            public IReadOnlyList<string> Keywords { get; set; } = Array.Empty<string>();
            public decimal? MaxPrice { get; set; }
            public decimal? MinPrice { get; set; }
            public bool WantsDelivery { get; set; }
            public bool WantsProducts { get; set; }
            public bool WantsCheapest { get; set; }
            public bool WantsFastest { get; set; }
            public bool WantsAffordable { get; set; }
        }
    }
}
