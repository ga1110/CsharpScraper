using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Searcher.Models;

namespace Searcher.Services.Reranking;

public sealed class QwenRelevanceLabeler : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private QwenRelevanceLabeler(HttpClient httpClient, string modelName)
    {
        _httpClient = httpClient;
        _modelName = modelName;
    }

    public string BaseUrl => _httpClient.BaseAddress?.ToString() ?? string.Empty;
    public string ModelName => _modelName;

    public static async Task<QwenRelevanceLabeler?> CreateAsync(
        string? baseUrl = null,
        string? modelName = null,
        CancellationToken cancellationToken = default)
    {
        baseUrl ??= Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
        modelName ??= Environment.GetEnvironmentVariable("QWEN_MODEL") ?? "qwen2.5:0.5b";

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            Console.WriteLine($"[Qwen] Некорректный URL: {baseUrl}");
            return null;
        }

        var client = new HttpClient
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(45)
        };

        var labeler = new QwenRelevanceLabeler(client, modelName);
        if (!await labeler.IsReachableAsync(cancellationToken))
        {
            labeler.Dispose();
            Console.WriteLine($"[Qwen] Не удалось подключиться к {baseUrl}");
            return null;
        }

        Console.WriteLine($"[Qwen] Relevance judge готов. Модель: {modelName}");
        return labeler;
    }

    public async Task<RelevancePrediction> EvaluateAsync(
        string query,
        ArticleDocument doc,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(query, doc);
        var payload = new
        {
            model = _modelName,
            prompt,
            stream = false,
            options = new
            {
                temperature = 0.1,
                top_p = 0.9,
                max_tokens = 200,
                repeat_penalty = 1.05f
            }
        };

        try
        {
            var json = JsonSerializer.Serialize(payload, _serializerOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("/api/generate", content, cancellationToken);
            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return RelevancePrediction.Error($"HTTP {(int)response.StatusCode}: {rawBody}");
            }

            var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(rawBody, _serializerOptions);
            return ParsePrediction(ollamaResponse?.Response, query, doc);
        }
        catch (TaskCanceledException)
        {
            return RelevancePrediction.Error("Таймаут запроса к Qwen");
        }
        catch (Exception ex)
        {
            return RelevancePrediction.Error(ex.Message);
        }
    }

    private async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/api/tags", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildPrompt(string query, ArticleDocument doc)
    {
        const string instructions = """
Ты судья релевантности результатов поиска. Оцени соответствие документа запросу пользователя.
Верни только компактный JSON:
{"label":0|1|2,"confidence":0-1,"reason":"краткий текст"}
Где:
  - label=0 (нерелевантно) если документ не отвечает на запрос
  - label=1 (частично) если документ затрагивает тему, но косвенно
  - label=2 (релевантно) если документ напрямую раскрывает тему запроса
confidence – твоя уверенность (0..1). reason – объяснение на русском до 20 слов.
Не добавляй текст до или после JSON. Не используй комментарии.
""";

        var snippet = BuildSnippet(doc.Content);
        var builder = new StringBuilder();
        builder.AppendLine(instructions);
        builder.AppendLine($"Запрос: {query}");
        builder.AppendLine("Документ:");
        builder.AppendLine($"Заголовок: {doc.Title}");
        if (!string.IsNullOrWhiteSpace(doc.Category))
            builder.AppendLine($"Категория: {doc.Category}");
        if (!string.IsNullOrWhiteSpace(doc.Author))
            builder.AppendLine($"Автор: {doc.Author}");
        builder.AppendLine($"Ссылка: {doc.Url}");
        builder.AppendLine($"Фрагмент: {snippet}");

        return builder.ToString();
    }

    private static string BuildSnippet(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "[нет текста]";

        var normalized = content.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length <= 600)
            return normalized;

        return normalized[..600];
    }

    private static RelevancePrediction ParsePrediction(string? response, string query, ArticleDocument doc)
    {
        if (string.IsNullOrWhiteSpace(response))
            return RelevancePrediction.Error("Пустой ответ модели");

        try
        {
            var cleaned = response.Trim('`', '\n', ' ');
            var jsonStart = cleaned.IndexOf('{');
            var jsonEnd = cleaned.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd >= jsonStart)
            {
                cleaned = cleaned.Substring(jsonStart, jsonEnd - jsonStart + 1);
            }

            var prediction = JsonSerializer.Deserialize<ModelPrediction>(cleaned);
            if (prediction == null)
                return RelevancePrediction.Error("Невозможно распарсить JSON");

            var label = Math.Clamp(prediction.Label, 0, 2);
            var confidence = Math.Clamp(prediction.Confidence ?? 0.5, 0, 1);
            return RelevancePrediction.Success(label, (float)confidence, prediction.Reason ?? string.Empty);
        }
        catch (Exception ex)
        {
            return RelevancePrediction.Error($"Парсинг ответа: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record OllamaResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; init; }
    }

    private sealed record ModelPrediction
    {
        [JsonPropertyName("label")]
        public int Label { get; init; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}

public sealed record RelevancePrediction
{
    private RelevancePrediction(bool isSuccess, int label, float confidence, string reason, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Label = label;
        Confidence = confidence;
        Reason = reason;
        ErrorMessage = errorMessage;
    }

    public bool IsSuccess { get; }
    public int Label { get; }
    public float Confidence { get; }
    public string Reason { get; } = string.Empty;
    public string? ErrorMessage { get; }

    public static RelevancePrediction Success(int label, float confidence, string reason) =>
        new(true, label, confidence, reason, null);

    public static RelevancePrediction Error(string message) =>
        new(false, 0, 0, string.Empty, message);
}

