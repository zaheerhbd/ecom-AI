using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services
{
    public class OpenAiAnswerGeneratorService : IAiAnswerGeneratorService
    {
        private const int MaxAttempts = 3;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OpenAiAnswerGeneratorService> _logger;
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _model;

        public OpenAiAnswerGeneratorService(
            IHttpClientFactory httpClientFactory,
            ILogger<OpenAiAnswerGeneratorService> logger,
            IConfiguration config)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _apiKey = config["OpenAI:ApiKey"];
            _baseUrl = config["OpenAI:BaseUrl"] ?? "https://api.openai.com/";
            _model = config["OpenAI:ChatModel"] ?? "gpt-5.4-mini";
        }

        public async Task<string> GenerateAnswerAsync(
            string question,
            IReadOnlyList<AiDocumentChunk> sources,
            string fallbackAnswer)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || sources == null || sources.Count == 0)
            {
                return fallbackAnswer;
            }

            var payload = new OpenAiResponseRequest
            {
                Model = _model,
                Input = new OpenAiPromptBuilder()
                    .WithQuestion(question)
                    .WithSources(sources)
                    .Build(),
                MaxOutputTokens = 350
            };

            var json = JsonSerializer.Serialize(payload);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                using var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(_baseUrl);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync("v1/responses", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    var answer = ExtractAnswer(responseJson);

                    if (!string.IsNullOrWhiteSpace(answer))
                    {
                        return answer.Trim();
                    }

                    _logger.LogWarning("OpenAI answer generation returned no text. Falling back to template answer.");
                    return fallbackAnswer;
                }

                if (!ShouldRetry(response.StatusCode) || attempt == MaxAttempts)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "OpenAI answer generation failed with status {StatusCode} on attempt {Attempt}. Falling back to template answer. Response: {ResponseBody}",
                        (int)response.StatusCode,
                        attempt,
                        Truncate(responseBody, 500));

                    return fallbackAnswer;
                }

                var delay = GetRetryDelay(response, attempt);
                _logger.LogWarning(
                    "OpenAI answer generation hit status {StatusCode} on attempt {Attempt}. Retrying after {DelayMs}ms.",
                    (int)response.StatusCode,
                    attempt,
                    (int)delay.TotalMilliseconds);

                await Task.Delay(delay);
            }

            return fallbackAnswer;
        }

        private static string ExtractAnswer(string responseJson)
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;

            if (root.TryGetProperty("output_text", out var outputText)
                && outputText.ValueKind == JsonValueKind.String)
            {
                return outputText.GetString();
            }

            if (!root.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parts = new List<string>();

            foreach (var outputItem in output.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(text.GetString());
                    }
                }
            }

            return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private static bool ShouldRetry(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.TooManyRequests
                || statusCode == HttpStatusCode.RequestTimeout
                || statusCode == HttpStatusCode.BadGateway
                || statusCode == HttpStatusCode.ServiceUnavailable
                || statusCode == HttpStatusCode.GatewayTimeout;
        }

        private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
        {
            if (response.Headers.RetryAfter?.Delta != null)
            {
                return response.Headers.RetryAfter.Delta.Value;
            }

            if (response.Headers.RetryAfter?.Date != null)
            {
                var delay = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    return delay;
                }
            }

            return TimeSpan.FromSeconds(Math.Min(attempt * 2, 10));
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }

        private class OpenAiResponseRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; }

            [JsonPropertyName("input")]
            public string Input { get; set; }

            [JsonPropertyName("max_output_tokens")]
            public int MaxOutputTokens { get; set; }
        }
    }
}
