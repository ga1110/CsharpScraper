 using System.Text;
using System.Text.Json;
using Searcher.Models;
using Searcher.Services.Evaluation;
using Searcher.Services.Search;

namespace Searcher.Services.Reranking;

/// <summary>
/// Сервис для подготовки датасета для fine-tuning нейросети на основе оценок релевантности пользователей
/// </summary>
public sealed class FineTuningDatasetBuilder
{
    private readonly EvaluationService _evaluationService;
    private readonly ElasticSearchService _searchService;
    private readonly string _datasetPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public FineTuningDatasetBuilder(
        EvaluationService evaluationService,
        ElasticSearchService searchService,
        string? datasetPath = null)
    {
        _evaluationService = evaluationService;
        _searchService = searchService;
        _datasetPath = ResolveDatasetPath(datasetPath);
    }

    public string DatasetPath => _datasetPath;

    /// <summary>
    /// Создает датасет для fine-tuning из оценок пользователей
    /// </summary>
    /// <param name="minRelevanceScore">Минимальная оценка релевантности для включения в датасет (по умолчанию 0 - все оценки)</param>
    /// <param name="cancellationToken">Токен отмены</param>
    /// <returns>Отчет о создании датасета</returns>
    public async Task<FineTuningDatasetReport> BuildFromEvaluationsAsync(
        int minRelevanceScore = 0,
        CancellationToken cancellationToken = default)
    {
        var evaluations = await _evaluationService.LoadAllEvaluationsAsync();
        
        if (evaluations.Count == 0)
        {
            return new FineTuningDatasetReport(0, 0, 0);
        }

        Console.WriteLine($"[Fine-tuning] Найдено {evaluations.Count} оцененных запросов");
        
        // Создаем директорию для датасета
        var directory = Path.GetDirectoryName(_datasetPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var totalExamples = 0;
        var successfulExamples = 0;
        var failedExamples = 0;

        await using var stream = new FileStream(_datasetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);

        foreach (var evaluation in evaluations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var result in evaluation.Results)
            {
                // Пропускаем результаты с низкой оценкой релевантности
                if (result.RelevanceScore < minRelevanceScore)
                    continue;

                totalExamples++;

                try
                {
                    // Получаем полный документ из ElasticSearch
                    var document = await GetDocumentByIdAsync(result.DocumentId, cancellationToken);
                    if (document == null)
                    {
                        Console.WriteLine($"[Fine-tuning] Документ {result.DocumentId} не найден в индексе");
                        failedExamples++;
                        continue;
                    }

                    // Создаем пример для обучения
                    var trainingExample = CreateTrainingExample(
                        evaluation.QueryText,
                        document,
                        result.RelevanceScore,
                        result.Comment);

                    // Сохраняем в JSONL формате
                    var jsonLine = JsonSerializer.Serialize(trainingExample, _jsonOptions);
                    await writer.WriteLineAsync(jsonLine);
                    successfulExamples++;

                    if (successfulExamples % 10 == 0)
                    {
                        Console.WriteLine($"[Fine-tuning] Обработано примеров: {successfulExamples}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Fine-tuning] Ошибка при обработке примера: {ex.Message}");
                    failedExamples++;
                }
            }
        }

        await writer.FlushAsync();

        Console.WriteLine($"[Fine-tuning] Датасет создан: {_datasetPath}");
        Console.WriteLine($"[Fine-tuning] Всего примеров: {totalExamples}, успешно: {successfulExamples}, ошибок: {failedExamples}");

        return new FineTuningDatasetReport(totalExamples, successfulExamples, failedExamples);
    }

    /// <summary>
    /// Создает пример для обучения в формате инструкция-ответ
    /// </summary>
    private static FineTuningExample CreateTrainingExample(
        string query,
        ArticleDocument document,
        int relevanceScore,
        string? userComment)
    {
        var prompt = BuildPrompt(query, document);
        
        // Создаем ожидаемый ответ на основе оценки пользователя
        var labelDescription = relevanceScore switch
        {
            0 => "нерелевантно",
            1 => "частично релевантно",
            2 => "очень релевантно",
            _ => "нерелевантно"
        };

        var reason = !string.IsNullOrWhiteSpace(userComment)
            ? userComment
            : relevanceScore switch
            {
                0 => "Документ не отвечает на запрос",
                1 => "Документ затрагивает тему, но косвенно",
                2 => "Документ напрямую раскрывает тему запроса",
                _ => "Неизвестная оценка"
            };

        // Формируем ожидаемый ответ модели
        var expectedResponse = new
        {
            label = relevanceScore,
            confidence = relevanceScore switch
            {
                0 => 0.1,
                1 => 0.6,
                2 => 0.9,
                _ => 0.5
            },
            reason = reason
        };

        var responseJson = JsonSerializer.Serialize(expectedResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return new FineTuningExample
        {
            Instruction = prompt,
            Response = responseJson,
            Input = query,
            Output = responseJson
        };
    }

    /// <summary>
    /// Строит промпт для оценки релевантности (аналогично QwenRelevanceLabeler)
    /// </summary>
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

    /// <summary>
    /// Получает документ из ElasticSearch по ID
    /// </summary>
    private async Task<ArticleDocument?> GetDocumentByIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Используем GetAsync для получения документа по ID
            var response = await _searchService.Client.GetAsync<ArticleDocument>(
                documentId,
                g => g.Index("articles"),
                cancellationToken);

            if (response.IsValidResponse && response.Source != null)
            {
                var doc = response.Source;
                doc.Id = documentId;
                return doc;
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fine-tuning] Ошибка получения документа {documentId}: {ex.Message}");
            return null;
        }
    }

    private static string ResolveDatasetPath(string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
            return Path.GetFullPath(customPath);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return Path.Combine(projectRoot, "data", "finetuning", "qwen_relevance_dataset.jsonl");
    }
}

/// <summary>
/// Пример для fine-tuning в формате инструкция-ответ
/// </summary>
public sealed class FineTuningExample
{
    public string Instruction { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
}

/// <summary>
/// Отчет о создании датасета для fine-tuning
/// </summary>
public sealed record FineTuningDatasetReport(
    int TotalExamples,
    int SuccessfulExamples,
    int FailedExamples);

