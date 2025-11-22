using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using Searcher.Models;

namespace Searcher.Services.SpellChecking;

/// <summary>
/// Клиент для обращения к локальному Ollama серверу и исправления опечаток с помощью модели Qwen2.5 (0.5B параметров).
/// </summary>
public sealed class OllamaSpellChecker : ISpellChecker, IDisposable
{
    public int Priority => 10; // LLM как последний fallback
    public string Name => "Ollama";
    
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    private OllamaSpellChecker(Uri baseUri, string modelName)
    {
        BaseUrl = baseUri.ToString().TrimEnd('/');
        ModelName = modelName;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public string BaseUrl { get; }

    public string ModelName { get; }

    /// <summary>
    /// Создаёт экземпляр spell-checker, если Ollama доступна по указанному адресу.
    /// </summary>
    public static async Task<OllamaSpellChecker?> TryCreateAsync(
        string? baseUrl = null,
        string? modelName = null,
        CancellationToken cancellationToken = default)
    {
        baseUrl ??= Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
        modelName ??= Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen2.5:0.5b";

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            Console.WriteLine($"[Ollama] Некорректный URL: {baseUrl}. Опечаточник отключен.");
            return null;
        }

        var checker = new OllamaSpellChecker(uri, modelName);
        if (!await checker.IsReachableAsync(cancellationToken).ConfigureAwait(false))
        {
            Console.WriteLine($"[Ollama] Не удалось подключиться к {checker.BaseUrl}. Опечаточник отключен.");
            checker.Dispose();
            return null;
        }

        Console.WriteLine($"[Ollama] Spell-check готов. Модель: {checker.ModelName}");
        return checker;
    }

    /// <summary>
    /// Пытается исправить запрос, используя модель Ollama.
    /// </summary>
    public async Task<SpellCheckResult> TryCorrectAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return SpellCheckResult.NoChange(query, "ollama");

        var normalized = query.Trim().ToLowerInvariant();
        if (_cache.TryGetValue(normalized, out var cached))
        {
            return SpellCheckResult.Correction(query, cached, "ollama-cache");
        }

        try
        {
            var requestPayload = BuildRequestPayload(query);
            var requestJson = JsonSerializer.Serialize(requestPayload, _serializerOptions);
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("/api/generate", content, cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return SpellCheckResult.Error(query, "ollama", $"HTTP {(int)response.StatusCode}: {body}");
            }

            var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(body, _serializerOptions);
            var suggestion = CleanSuggestion(ollamaResponse?.Response, query);

            if (string.Equals(suggestion, query, StringComparison.OrdinalIgnoreCase))
            {
                return SpellCheckResult.NoChange(query, "ollama");
            }

            _cache.AddOrUpdate(normalized, suggestion, (_, _) => suggestion);
            return SpellCheckResult.Correction(query, suggestion, "ollama");
        }
        catch (TaskCanceledException)
        {
            return SpellCheckResult.Error(query, "ollama", "Таймаут запроса к Ollama");
        }
        catch (Exception ex)
        {
            return SpellCheckResult.Error(query, "ollama", ex.Message);
        }
    }

    private async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/api/version", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private object BuildRequestPayload(string query)
    {
        return new
        {
            model = ModelName,
            prompt = BuildPrompt(query),
            stream = false,
            options = new
            {
                temperature = 0.1,
                top_p = 0.9,
                max_tokens = 64,
                repeat_penalty = 1.05
            }
        };
    }

    private static string BuildPrompt(string query)
    {
        const string instructions = """
Исправь опечатки в поисковом запросе на русском языке для новостного сайта.
Контекст: политика, общество, Россия, Украина, США, Европа.
Верни только исправленный запрос без кавычек и объяснений.
Если опечаток нет, верни исходный запрос.
""";
        return $"{instructions}\nЗапрос: {query}\nОтвет:";
    }

    private static string CleanSuggestion(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        var firstLine = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? raw;

        var cleaned = firstLine.Trim().Trim('\'', '"', '`');

        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
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
}

