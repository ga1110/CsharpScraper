using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Ранжирует список документов на основе оценки релевантности нейросетью
    /// </summary>
    /// <param name="query">Поисковый запрос</param>
    /// <param name="documents">Список документов для ранжирования</param>
    /// <param name="cancellationToken">Токен отмены</param>
    /// <returns>Отсортированный список документов по релевантности</returns>
    public async Task<List<ArticleDocument>> RerankAsync(
        string query,
        List<ArticleDocument> documents,
        CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0)
            return documents;

        // Оцениваем каждый документ через нейросеть параллельно для ускорения
        var evaluationTasks = documents.Select(async doc =>
        {
            var prediction = await EvaluateAsync(query, doc, cancellationToken);
            
            // Вычисляем комбинированный score: label (0-2) * confidence (0-1)
            // Это даёт диапазон от 0 до 2, где выше = лучше
            double score = 0;
            if (prediction.IsSuccess)
            {
                score = prediction.Label * prediction.Confidence;
                // Сохраняем score в документ для отладки
                doc.RerankerScore = score;
            }
            else
            {
                // Если оценка не удалась, используем исходный ElasticScore
                score = doc.ElasticScore;
            }

            return (doc, score);
        });

        var scoredDocuments = await Task.WhenAll(evaluationTasks);

        // Сортируем по убыванию score
        return scoredDocuments
            .OrderByDescending(x => x.score)
            .Select(x => x.doc)
            .ToList();
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
Внутри поля reason не используй кавычки, фигурные/квадратные скобки или перевод строки, только простой текст.
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

            // Сначала пробуем строгий JSON‑парсинг
            var prediction = JsonSerializer.Deserialize<ModelPrediction>(cleaned);
            if (prediction == null)
                return RelevancePrediction.Error("Невозможно распарсить JSON");

            var label = Math.Clamp(prediction.Label, 0, 2);
            var confidence = Math.Clamp(prediction.Confidence ?? 0.5, 0, 1);
            return RelevancePrediction.Success(label, (float)confidence, prediction.Reason ?? string.Empty);
        }
        catch (Exception ex)
        {
            // Фоллбек: пытаемся вытащить label/confidence/reason из произвольного текста регулярками
            if (TryParseLoose(response, out var label, out var confidence, out var reason))
            {
                return RelevancePrediction.Success(label, confidence, reason ?? string.Empty);
            }

            return RelevancePrediction.Error($"Парсинг ответа: {ex.Message}");
        }
    }

    private static bool TryParseLoose(string raw, out int label, out float confidence, out string? reason)
    {
        label = 0;
        confidence = 0.5f;
        reason = null;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Replace("\r", " ").Replace("\n", " ");

        var labelMatch = Regex.Match(text, @"label\s*[:=]\s*(\d)", RegexOptions.IgnoreCase);
        if (!labelMatch.Success || !int.TryParse(labelMatch.Groups[1].Value, out var parsedLabel))
            return false;

        var confMatch = Regex.Match(text, @"confidence\s*[:=]\s*([0-9]*\.?[0-9]+)", RegexOptions.IgnoreCase);
        double parsedConfidence = 0.5;
        if (confMatch.Success && double.TryParse(confMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var c))
        {
            parsedConfidence = c;
        }

        var reasonMatch = Regex.Match(text, @"reason\s*[:=]\s*""([^""]*)""", RegexOptions.IgnoreCase);
        if (reasonMatch.Success)
        {
            reason = reasonMatch.Groups[1].Value.Trim();
        }

        label = Math.Clamp(parsedLabel, 0, 2);
        confidence = (float)Math.Clamp(parsedConfidence, 0.0, 1.0);
        return true;
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

