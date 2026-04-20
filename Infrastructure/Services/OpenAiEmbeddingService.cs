using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services
{
    public class OpenAiEmbeddingService : IAiEmbeddingService
    {
        private const int MaxAttempts = 3;

        // HttpClientFactory creates HttpClient objects safely for API calls.
        private readonly IHttpClientFactory _httpClientFactory;

        // This fallback keeps the app usable if OpenAI is not configured yet.
        private readonly IAiEmbeddingService _fallbackEmbeddingService;
        private readonly ILogger<OpenAiEmbeddingService> _logger;
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _model;

        public OpenAiEmbeddingService(
            IHttpClientFactory httpClientFactory,
            LocalHashEmbeddingService fallbackEmbeddingService,
            ILogger<OpenAiEmbeddingService> logger,
            IConfiguration config)
        {
            _httpClientFactory = httpClientFactory;
            _fallbackEmbeddingService = fallbackEmbeddingService;
            _logger = logger;
            _apiKey = config["OpenAI:ApiKey"];
            _baseUrl = config["OpenAI:BaseUrl"] ?? "https://api.openai.com/";
            _model = config["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small";
        }

        public async Task<IReadOnlyList<IReadOnlyList<double>>> CreateEmbeddingsAsync(IReadOnlyList<string> inputs)
        {
            if (inputs == null || inputs.Count == 0)
            {
                return Array.Empty<IReadOnlyList<double>>();
            }

            // If no OpenAI key is configured yet, keep local development working with the fallback embeddings.
            // That means:
            // - with key    -> use real OpenAI embeddings
            // - without key -> use local hash embeddings
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                return await _fallbackEmbeddingService.CreateEmbeddingsAsync(inputs);
            }

            // Example input sent to OpenAI:
            // {
            //   "model": "text-embedding-3-small",
            //   "input": [
            //     "hat under 10",
            //     "Green React Woolen Hat Type: Hat. React Hat 8.00 /shop/8"
            //   ]
            // }
            var payload = new OpenAiEmbeddingRequest
            {
                Model = _model,
                Input = inputs.ToArray()
            };

            var json = JsonSerializer.Serialize(payload);
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                using var client = _httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(_baseUrl);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync("v1/embeddings", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<OpenAiEmbeddingResponse>(responseJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    // Example output shape:
                    // [
                    //   [0.01, -0.03, 0.12, ...],
                    //   [0.04, -0.01, 0.09, ...]
                    // ]
                    //
                    // These numbers come from the OpenAI embedding model.
                    if (result?.Data == null)
                    {
                        return Array.Empty<IReadOnlyList<double>>();
                    }

                    return result.Data
                        .OrderBy(item => item.Index)
                        .Select(item => (IReadOnlyList<double>)item.Embedding)
                        .ToList();
                }

                if (!ShouldRetry(response.StatusCode) || attempt == MaxAttempts)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "OpenAI embeddings request failed with status {StatusCode} on attempt {Attempt}. Falling back to local embeddings. Response: {ResponseBody}",
                        (int)response.StatusCode,
                        attempt,
                        Truncate(responseBody, 500));

                    return await _fallbackEmbeddingService.CreateEmbeddingsAsync(inputs);
                }

                var delay = GetRetryDelay(response, attempt);
                _logger.LogWarning(
                    "OpenAI embeddings request hit status {StatusCode} on attempt {Attempt}. Retrying after {DelayMs}ms.",
                    (int)response.StatusCode,
                    attempt,
                    (int)delay.TotalMilliseconds);

                await Task.Delay(delay);
            }

            return await _fallbackEmbeddingService.CreateEmbeddingsAsync(inputs);
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

        private class OpenAiEmbeddingRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; }

            [JsonPropertyName("input")]
            public string[] Input { get; set; }
        }

        private class OpenAiEmbeddingResponse
        {
            [JsonPropertyName("data")]
            public List<OpenAiEmbeddingItem> Data { get; set; } = new List<OpenAiEmbeddingItem>();
        }

        private class OpenAiEmbeddingItem
        {
            [JsonPropertyName("index")]
            public int Index { get; set; }

            [JsonPropertyName("embedding")]
            public double[] Embedding { get; set; }
        }
    }
}
