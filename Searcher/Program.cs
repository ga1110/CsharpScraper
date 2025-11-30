using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Searcher.Models;
using Searcher.Services.Evaluation;
using Searcher.Services.Search;
using Searcher.Services.Search.Indexing;
using Searcher.Services.SpellChecking;
using Searcher.Services.StopWords;
using Searcher.Services.Synonyms;
using Searcher.Services.Reranking;
using Searcher.Services.TextProcessing;
using Scraper.Models;
using Scraper.Services;

namespace Searcher;

class Program
{
    private static readonly char[] TagSeparators = new[] { '=', ':' };
    private static readonly StopWordsProvider StopWords = StopWordsProvider.CreateDefault();
    private static readonly SynonymProvider Synonyms = new SynonymProvider();
    private static CompositeSpellChecker? CompositeSpellChecker;
    private static RerankerService? Reranker;
    private static QwenRelevanceLabeler? RelevanceLabeler;

    /// <summary>
    /// Точка входа в приложение. Поддерживает два режима: индексацию статей (index) и интерактивный поиск
    /// </summary>

    static async Task Main(string[] args)
    {
        // Включаем поддержку UTF-8 в консоли, чтобы корректно отображать русские тексты и подсветки
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        // Загружаем синонимы при старте (из корня проекта)
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var synonymsPath = Path.GetFullPath(Path.Combine(projectRoot, "synonyms.json"));
        
        // Если файл не найден в корне, пробуем в текущей директории (для обратной совместимости)
        if (!File.Exists(synonymsPath))
        {
            var localPath = Path.GetFullPath("synonyms.json");
            if (File.Exists(localPath))
            {
                synonymsPath = localPath;
            }
        }
        
        Synonyms.LoadFromFile(synonymsPath);

        if (args.Length > 0 && args[0].Equals("mine-synonyms", StringComparison.OrdinalIgnoreCase))
        {
            var command = string.Join(' ', args);
            await HandleMineSynonymsCommand(command, Synonyms);
            return;
        }

        // Подготавливаем сервисы ElasticSearch и индексации, чтобы использовать их в обоих режимах работы
        var elasticSearchService = new ElasticSearchService(
            username: "elastic",
            password: "muVmg+YxSgExd2NKBttV",
            synonymProvider: Synonyms
        );
        var indexingService = new IndexingService(elasticSearchService);

        // Инициализируем композитный проверщик орфографии
        CompositeSpellChecker = new CompositeSpellChecker(searchService: elasticSearchService);
        
        // Пытаемся добавить Ollama, если доступна
        var ollamaChecker = await OllamaSpellChecker.TryCreateAsync();
        if (ollamaChecker != null)
        {
            CompositeSpellChecker.AddChecker(ollamaChecker);
            Console.WriteLine("Ollama spell checker подключен");
        }
        else
        {
            Console.WriteLine("Ollama недоступна, используем только традиционные методы");
        }

        Reranker = new RerankerService();
        if (Reranker.TryLoad())
        {
            Console.WriteLine("[Reranker] Модель найдена и загружена. Реранкер готов к работе.");
        }
        else
        {
            Console.WriteLine("[Reranker] Модель не найдена. Для обучения используйте команду 'train-reranker'.");
        }

        // Инициализируем Qwen Relevance Labeler для автоматической оценки релевантности
        RelevanceLabeler = await QwenRelevanceLabeler.CreateAsync();

        var backupService = new ElasticsearchBackupService(elasticSearchService.Client);

        // Перед работой проверяем доступность кластера и сообщаем пользователю диагностическую информацию
        Console.WriteLine("Проверка подключения к ElasticSearch");
        try
        {
            // Простой ping даёт быстрый ответ о том, жив ли кластер
            if (!await elasticSearchService.PingAsync())
            {
                Console.WriteLine("Ошибка подключения к ElasticSearch");
                return;
            }
            
            // Если пинг успешен — выводим количество документов, чтобы понимать текущее состояние индекса
            var health = await elasticSearchService.GetTotalDocumentsAsync();
            Console.WriteLine($"Подключение успешно. Документов в индексе: {health}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка подключения к ElasticSearch: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Детали: {ex.InnerException.Message}");
            }
            Console.WriteLine();
            Console.WriteLine("Убедитесь, что ElasticSearch запущен и доступен на http://localhost:9200");
            return;
        }

        if (args.Length > 0 && args[0] == "index" && args.Length > 1)
        {
            await indexingService.IndexArticlesFromJsonAsync(args[1]);
            return;
        }

        if (args.Length > 0 && args[0].Equals("build-reranker", StringComparison.OrdinalIgnoreCase))
        {
            var command = string.Join(' ', args);
            await HandleBuildRerankerCommand(command, elasticSearchService);
            return;
        }

        if (args.Length > 0 && args[0].Equals("train-reranker", StringComparison.OrdinalIgnoreCase))
        {
            var command = string.Join(' ', args);
            await HandleTrainRerankerCommand(command);
            return;
        }

        ShowMainMenu();

        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input.ToLower() == "exit")
                break;

            if (input.ToLower().StartsWith("scrape "))
                // Команда scrape запускает сбор свежих статей и их последующую индексацию
                await HandleScrapeCommand(input, elasticSearchService);
            else if (input.ToLower().Trim() == "index")
                // Команда index переиспользует существующий JSON, чтобы быстро перестроить индекс
                await HandleIndexCommand(indexingService, elasticSearchService);
            else if (input.ToLower().StartsWith("mine-synonyms"))
                // Команда mine-synonyms запускает автоматический майнинг синонимов
                await HandleMineSynonymsCommand(input, Synonyms);
            else if (input.ToLower().StartsWith("search "))
                // Команда search выполняет полнотекстовый поиск и выводит результаты в консоль
                await HandleSearchCommand(input, elasticSearchService, Synonyms);
            else if (input.ToLower().StartsWith("backup"))
                // Команда backup создает бэкап индекса
                await HandleBackupCommand(input, backupService);
            else if (input.ToLower().StartsWith("restore"))
                // Команда restore восстанавливает индекс из бэкапа
                await HandleRestoreCommand(input, backupService);
            else if (input.ToLower().Trim() == "stats")
                // Команда stats показывает статистику spell checker
                HandleStatsCommand();
            else if (input.ToLower().StartsWith("evaluate "))
                // Команда evaluate выполняет поиск и сразу оценивает результаты
                await HandleEvaluateCommand(input, elasticSearchService);
            else if (input.ToLower().Trim() == "show-metrics")
                // Команда show-metrics показывает все рассчитанные метрики
                await HandleShowMetricsCommand();
            else if (input.ToLower().StartsWith("save-report "))
                // Команда save-report сохраняет полный отчет в файл
                await HandleSaveReportCommand(input);
            else if (input.ToLower().StartsWith("export-evaluation "))
                // Команда export-evaluation экспортирует данные в CSV
                await HandleExportEvaluationCommand(input);
            else if (input.ToLower().StartsWith("build-reranker"))
                // Сбор датасета для reranker-модели
                await HandleBuildRerankerCommand(input, elasticSearchService);
            else if (input.ToLower().StartsWith("train-reranker"))
                // Обучение reranker-модели и сохранение на диск
                await HandleTrainRerankerCommand(input);
            else if (input.ToLower().StartsWith("build-finetuning"))
                // Подготовка датасета для fine-tuning нейросети из оценок пользователей
                await HandleBuildFineTuningCommand(input, elasticSearchService);
            else if (input.ToLower().Trim() == "clear-evaluation")
                // Команда clear-evaluation очищает все данные оценки
                await HandleClearEvaluationCommand();
            else if (input.ToLower().Trim() == "clear")
                // Команда clear очищает консоль и показывает меню
                HandleClearCommand();
            else
                Console.WriteLine("Неизвестная команда. Используйте 'scrape', 'index', 'mine-synonyms', 'search', 'backup', 'restore', 'stats', 'evaluate', 'show-metrics', 'save-report', 'export-evaluation', 'build-reranker', 'train-reranker', 'build-finetuning', 'clear-evaluation', 'clear' или 'exit'");
        }
    }

    /// <summary>
    /// Обрабатывает команду поиска, поддерживая теги category/author/size в любом порядке и формате записи
    /// </summary>
    /// <param name="command">Строка команды поиска в формате "search <запрос> [category=<категория>] [author=<автор>] [size=<число>]"</param>
    /// <param name="elasticSearchService">Сервис для выполнения поиска в ElasticSearch</param>
    /// <param name="synonyms">Провайдер синонимов для расширения запросов</param>
    static async Task HandleSearchCommand(string command, ElasticSearchService elasticSearchService, SynonymProvider synonyms)
    {
        var arguments = command.Length > "search".Length
            ? command.Substring("search".Length).Trim()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(arguments))
        {
            PrintSearchUsage();
            return;
        }

        var tokens = SplitArguments(arguments);

        if (tokens.Count == 0)
        {
            PrintSearchUsage();
            return;
        }

        var queryParts = new List<string>();
        int size = 10;
        string? category = null;
        string? author = null;
        double? synonymConfidence = null;

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (TryParseSearchTag(token, out var tagType, out var inlineValue, out var requiresNextValue))
            {
                string? tagValue = inlineValue;

                if (requiresNextValue)
                {
                    if (i + 1 >= tokens.Count)
                    {
                        Console.WriteLine($"Тег '{GetTagLabel(tagType)}' требует значение. Пример: category=\"Новости\"");
                        return;
                    }

                    tagValue = tokens[++i];
                }

                if (string.IsNullOrWhiteSpace(tagValue))
                    continue;

                switch (tagType)
                {
                    case SearchTagType.Category:
                        category = TextPreprocessor.NormalizeOrNull(tagValue);
                        break;
                    case SearchTagType.Author:
                        author = TextPreprocessor.NormalizeOrNull(tagValue);
                        break;
                    case SearchTagType.Size:
                        if (int.TryParse(tagValue, out var parsedSize) && parsedSize > 0)
                            size = parsedSize;
                        else
                            Console.WriteLine($"Размер должен быть положительным числом. Игнорируем значение '{tagValue}'.");
                        break;
                    case SearchTagType.SynonymConfidence:
                        if (double.TryParse(tagValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedThreshold))
                        {
                            synonymConfidence = Math.Clamp(parsedThreshold, 0.0, 1.0);
                        }
                        else
                        {
                            Console.WriteLine($"Порог синонимов должен быть числом между 0 и 1. Игнорируем '{tagValue}'.");
                        }
                        break;
                }

                continue;
            }

            var normalizedToken = TextPreprocessor.Normalize(token);
            if (StopWords.IsStopWord(normalizedToken))
                continue;

            if (!string.IsNullOrEmpty(normalizedToken))
                queryParts.Add(normalizedToken);
        }

        var query = string.Join(' ', queryParts).Trim();

        if (string.IsNullOrEmpty(query))
        {
            Console.WriteLine("Запрос не может быть пустым!");
            return;
        }

        if (CompositeSpellChecker != null && query.Length >= 3)
        {
            try
            {
                var correction = await CompositeSpellChecker.TryCorrectAsync(query);
                if (correction.HasCorrection)
                {
                    Console.WriteLine($"Исправлен запрос: '{query}' -> '{correction.CorrectedQuery}'");
                    Console.WriteLine($"Уверенность: {correction.Confidence:F2}, Время: {correction.ProcessingTime.TotalMilliseconds:F0}мс");
                    
                    if (correction.Steps.Count > 0)
                    {
                        Console.WriteLine("Шаги исправления:");
                        foreach (var step in correction.Steps)
                        {
                            Console.WriteLine($"  {step.Method}: '{step.Before}' -> '{step.After}' ({step.Confidence:F2})");
                        }
                    }
                    
                    query = correction.CorrectedQuery;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при исправлении опечаток: {ex.Message}");
            }
        }

        // Расширяем запрос синонимами
        var expandedQuery = synonyms.ExpandQuery(query, synonymConfidence);
        
        Console.WriteLine($"Оригинальный запрос: '{query}'");
        if (expandedQuery != query)
        {
            Console.WriteLine($"Расширенный запрос (с синонимами): '{expandedQuery}'");
        }
        Console.WriteLine($"Поиск: '{expandedQuery}' (результатов: {size})");
        if (!string.IsNullOrEmpty(category))
            Console.WriteLine($"Категория: {category}");
        if (!string.IsNullOrEmpty(author))
            Console.WriteLine($"Автор: {author}");
        if (synonymConfidence.HasValue)
            Console.WriteLine($"Минимальная уверенность синонимов: {synonymConfidence.Value:F2}");
        Console.WriteLine();

        try
        {
            // Выполняем запрос в ElasticSearch, передавая при необходимости смещение, фильтры и размер выдачи
            var result = await elasticSearchService.SearchAsync(expandedQuery, 0, size, category, author);
            
            // Записываем статистику для обучения spell checker
            var analyticsChecker = CompositeSpellChecker?._checkers?.OfType<SearchAnalyticsSpellChecker>().FirstOrDefault();
            if (analyticsChecker != null)
            {
                var wasSuccessful = result.Total > 0;
                var originalQuery = arguments.Split(' ').Where(t => !t.Contains('=')).FirstOrDefault() ?? query;
                var correctedFrom = !string.Equals(originalQuery, expandedQuery, StringComparison.OrdinalIgnoreCase) ? originalQuery : null;
                analyticsChecker.RecordSearch(expandedQuery, (int)result.Total, wasSuccessful, correctedFrom);
            }
            
            Console.WriteLine($"Найдено документов: {result.Total}");
            Console.WriteLine($"Показано: {result.Documents.Count}");
            Console.WriteLine(new string('-', 80));

            // Используем нейросеть для ранжирования, если доступна
            if (RelevanceLabeler != null && result.Documents.Count > 1)
            {
                Console.WriteLine("[Нейросеть] Переупорядочивание результатов...");
                try
                {
                    result.Documents = await RelevanceLabeler.RerankAsync(query, result.Documents);
                    Console.WriteLine("[Нейросеть] Порядок результатов обновлён на основе оценки релевантности.");
                    Console.WriteLine(new string('-', 80));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Нейросеть] Ошибка ранжирования: {ex.Message}");
                    Console.WriteLine("[Нейросеть] Используется исходный порядок результатов.");
                    Console.WriteLine(new string('-', 80));
                }
            }
            // Если нейросеть недоступна, используем ML.NET reranker
            else if (Reranker?.IsReady == true && result.Documents.Count > 1)
            {
                result.Documents = Reranker.Rerank(query, result.Documents);
                Console.WriteLine("[Reranker] Порядок результатов обновлён ML-моделью.");
                Console.WriteLine(new string('-', 80));
            }

            for (int i = 0; i < result.Documents.Count; i++)
            {
                var doc = result.Documents[i];
                // Для каждого результата пробуем показать подсветки; если их нет — выводим короткий фрагмент контента
                var highlights = result.Highlights.ContainsKey(doc.Id) 
                    ? result.Highlights[doc.Id] 
                    : new List<string>();

                Console.WriteLine($"{i + 1}. {doc.Title}");
                Console.WriteLine($"   URL: {doc.Url}");
                if (!string.IsNullOrEmpty(doc.Category))
                    Console.WriteLine($"   Категория: {doc.Category}");
                if (!string.IsNullOrEmpty(doc.Author))
                    Console.WriteLine($"   Автор: {doc.Author}");
                if (doc.PublishDate.HasValue)
                    Console.WriteLine($"   Дата: {doc.PublishDate.Value:yyyy-MM-dd HH:mm}");
                if (doc.CommentCount.HasValue)
                    Console.WriteLine($"   Комментариев: {doc.CommentCount}");
                
                if (highlights.Any())
                {
                    // Подсветки нагляднее показывают, почему документ попал в выдачу
                    Console.WriteLine($"   Выделенные фрагменты:");
                    foreach (var highlight in highlights.Take(3))
                        Console.WriteLine($"     {highlight}");
                }
                else if (!string.IsNullOrEmpty(doc.Content))
                {
                    // Если подсветок нет, выводим превью первых 200 символов текста
                    var preview = doc.Content.Length > 200 
                        ? doc.Content.Substring(0, 200) + " [обрезано]" 
                        : doc.Content;
                    Console.WriteLine($"   Содержание: {preview}");
                }

                Console.WriteLine(new string('-', 80));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при поиске: {ex.Message}");
        }

        Console.WriteLine();
    }

    static void PrintSearchUsage()
    {
        Console.WriteLine("Использование: search <запрос> [category=<категория>] [author=<автор>] [size=<число>] [synmin=<0-1>]");
        Console.WriteLine("Теги: category=, author=, size=, synmin= или --category, --author, --size, --synmin");
    }

    static List<string> SplitArguments(string arguments)
    {
        var tokens = new List<string>();

        if (string.IsNullOrWhiteSpace(arguments))
            return tokens;

        // Простое разбиение по пробелам, кавычки не обязательны
        // Значения в кавычках сохраняются как один токен, но кавычки не обязательны
        var parts = arguments.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var part in parts)
        {
            // Убираем кавычки с краев, если они есть (для обратной совместимости)
            var trimmed = part.Trim();
            if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
                (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            }
            
            if (!string.IsNullOrEmpty(trimmed))
                tokens.Add(trimmed);
        }

        return tokens;
    }

    static Dictionary<string, string> ParseOptionDictionary(List<string> tokens)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                continue;

            var trimmed = token.TrimStart('-');
            string value = "true";

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex >= 0)
            {
                value = trimmed.Substring(separatorIndex + 1);
                trimmed = trimmed.Substring(0, separatorIndex);
            }
            else if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = tokens[++i];
            }

            dict[trimmed] = value.Trim('"');
        }

        return dict;
    }

    static string? GetOption(Dictionary<string, string> options, string key)
    {
        return options.TryGetValue(key, out var value) ? value : null;
    }

    static int GetIntOption(Dictionary<string, string> options, string key, int defaultValue)
    {
        if (options.TryGetValue(key, out var value) && int.TryParse(value, out var parsed))
            return parsed;
        return defaultValue;
    }

    static double GetDoubleOption(Dictionary<string, string> options, string key, double defaultValue)
    {
        if (options.TryGetValue(key, out var value) &&
            double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Clamp(parsed, 0.05, 0.5);
        }

        return defaultValue;
    }

    static string GetProjectRoot()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
    }

    static bool TryParseSearchTag(string rawToken, out SearchTagType tagType, out string? inlineValue, out bool expectsNextValue)
    {
        tagType = default;
        inlineValue = null;
        expectsNextValue = false;

        if (string.IsNullOrWhiteSpace(rawToken))
            return false;

        var token = NormalizeToken(rawToken);

        if (TryMatchInlineTag(token, out tagType, out inlineValue))
        {
            inlineValue = inlineValue?.Trim();
            return true;
        }

        if (TryMatchSeparatedTag(token, out tagType))
        {
            expectsNextValue = true;
            return true;
        }

        return false;
    }

    static string NormalizeToken(string token)
    {
        var trimmed = token.Trim();

        while (trimmed.StartsWith("["))
            trimmed = trimmed.Substring(1);

        while (trimmed.EndsWith("]"))
            trimmed = trimmed.Substring(0, trimmed.Length - 1);

        return trimmed;
    }

    static bool TryMatchInlineTag(string token, out SearchTagType tagType, out string? inlineValue)
    {
        tagType = default;
        inlineValue = null;

        if (string.IsNullOrEmpty(token))
            return false;

        var normalized = token;
        var dashCount = 0;
        while (dashCount < normalized.Length && normalized[dashCount] == '-')
            dashCount++;

        if (dashCount > 0)
            normalized = normalized.Substring(dashCount);

        foreach (var separator in TagSeparators)
        {
            var separatorIndex = normalized.IndexOf(separator);
            if (separatorIndex <= 0)
                continue;

            var alias = normalized.Substring(0, separatorIndex).Trim();
            var value = normalized.Substring(separatorIndex + 1).Trim();

            if (TryResolveTag(alias, out tagType))
            {
                inlineValue = value;
                return true;
            }
        }

        return false;
    }

    static bool TryMatchSeparatedTag(string token, out SearchTagType tagType)
    {
        tagType = default;

        if (!token.StartsWith("--", StringComparison.Ordinal))
            return false;

        var alias = token.Substring(2).Trim();

        return TryResolveTag(alias, out tagType);
    }

    static bool TryResolveTag(string candidate, out SearchTagType tagType)
    {
        if (candidate.Equals("category", StringComparison.OrdinalIgnoreCase))
        {
            tagType = SearchTagType.Category;
            return true;
        }

        if (candidate.Equals("author", StringComparison.OrdinalIgnoreCase))
        {
            tagType = SearchTagType.Author;
            return true;
        }

        if (candidate.Equals("size", StringComparison.OrdinalIgnoreCase))
        {
            tagType = SearchTagType.Size;
            return true;
        }

        if (candidate.Equals("synmin", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("synconf", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("synconfidence", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("synonymconfidence", StringComparison.OrdinalIgnoreCase))
        {
            tagType = SearchTagType.SynonymConfidence;
            return true;
        }

        tagType = default;
        return false;
    }

    static string GetTagLabel(SearchTagType tagType) => tagType switch
    {
        SearchTagType.Category => "category",
        SearchTagType.Author => "author",
        SearchTagType.Size => "size",
        SearchTagType.SynonymConfidence => "synmin",
        _ => "tag"
    };

    enum SearchTagType
    {
        Category,
        Author,
        Size,
        SynonymConfidence
    }

    /// <summary>
    /// Обрабатывает команду скрапинга статей и автоматически индексирует их в ElasticSearch
    /// </summary>
    /// <param name="command">Строка команды скрапинга в формате "scrape <количество> [--clear]"</param>
    /// <param name="elasticSearchService">Сервис для работы с ElasticSearch</param>
    static async Task HandleScrapeCommand(string command, ElasticSearchService elasticSearchService)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length < 2)
        {
            Console.WriteLine("Использование: scrape <количество> [--clear]");
            Console.WriteLine("  количество - количество статей для скрапинга и индексации");
            Console.WriteLine("  --clear - очистить articles.json перед сохранением новых статей");
            return;
        }

        // Флаг --clear заставляет очистить существующий JSON перед сохранением новых статей
        bool clearJson = parts.Any(p => p == "--clear");
        
        // Определяем количество статей, перебирая оставшиеся токены, пока не встретим валидное число
        int maxArticles = 0;
        foreach (var part in parts.Skip(1))
        {
            if (part == "--clear")
                continue;
            
            if (int.TryParse(part, out maxArticles) && maxArticles > 0)
                break;
        }

        if (maxArticles <= 0)
        {
            Console.WriteLine($"Ошибка: не удалось определить количество статей.");
            Console.WriteLine("Использование: scrape <количество> [--clear]");
            return;
        }
        
        // Определяем путь к корню решения, чтобы всегда сохранять статьи в один и тот же файл
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var jsonFilePath = Path.Combine(projectRoot, "articles.json");
        
        // Очищаем файл заранее, если пользователь запросил явную перезапись
        if (clearJson)
        {
            File.WriteAllText(jsonFilePath, "[]");
            Console.WriteLine("Файл очищен");
            Console.WriteLine();
        }
        
        try
        {
            // Создаём скрапер и сразу завернём его в using, чтобы корректно освободить ресурсы
            using var scraper = new Scraper.Services.Scraper();
        
            // Сбор статей может занять время, поэтому выполняем его асинхронно
            var articles = await scraper.ScrapeArticlesAsync(maxArticles);

            Console.WriteLine();
            Console.WriteLine($"Скрапинг завершен! Собрано статей: {articles.Count}");

            if (articles.Count == 0)
            {
                Console.WriteLine("Статьи не найдены.");
                return;
            }

            // Дополнительно проверяем наличие дубликатов URL и удаляем их, чтобы индекс не разрастался
            // После очистки и дедупликации сохраняем итоговый список в JSON
            
            var scraperUtils = new Scraper.Services.ScraperUtils();
            await scraperUtils.SaveToJsonAsync(articles, jsonFilePath);


            // Далее автоматически переиндексируем ElasticSearch, чтобы свежие данные стали доступны в поиске
            Console.WriteLine();
            Console.WriteLine("Начало индексации в ElasticSearch");
            
            // Перед индексацией гарантированно удаляем старый индекс, чтобы избежать конфликтов схемы
            Console.WriteLine("Очистка индекса перед индексацией");
            await elasticSearchService.DeleteIndexAsync();
            
            // Создаём новый индекс с корректным маппингом перед загрузкой документов
            if (!await elasticSearchService.EnsureIndexExistsAsync())
            {
                Console.WriteLine("Ошибка при создании индекса!");
                return;
            }

            // Индексируем статьи пакетами, чтобы не перегружать кластер
            const int batchSize = 100;
            int totalIndexed = 0;
            
            for (int i = 0; i < articles.Count; i += batchSize)
            {
                var batch = articles.Skip(i).Take(batchSize).ToList();
                // ElasticSearchService возвращает, сколько документов реально попало в индекс
                var (success, indexedCount) = await elasticSearchService.IndexArticlesAsync(batch);
                totalIndexed += indexedCount;
                Console.WriteLine($"Проиндексировано: {totalIndexed} из {articles.Count}");
            }

            Console.WriteLine();
            Console.WriteLine($"Индексация завершена! Проиндексировано статей: {totalIndexed}");

            
            // Даем ElasticSearch время обновить индекс перед проверкой
            await Task.Delay(1000);
            
            // Сверяем отчёт по индексации с фактическим количеством документов
            var totalDocs = await elasticSearchService.GetTotalDocumentsAsync();
            Console.WriteLine($"Всего документов в индексе: {totalDocs}");
            
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при скрапинге: {ex.Message}");
            if (ex.StackTrace != null)
                Console.WriteLine($"Стек вызовов: {ex.StackTrace}");
            // Даже при ошибке оставляем пустую строку, чтобы визуально отделить события
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Обрабатывает команду индексации статей из articles.json файла в корне проекта
    /// </summary>
    /// <param name="indexingService">Сервис для индексации статей</param>
    /// <param name="elasticSearchService">Сервис для работы с ElasticSearch</param>
    static async Task HandleIndexCommand(IndexingService indexingService, ElasticSearchService elasticSearchService)
    {
        // Определяем путь к корню проекта
        // Если запускается из bin/Debug, идем на 3 уровня выше (Searcher/bin/Debug -> Searcher -> Scraper)
        // Если запускается из проекта напрямую, идем на 1 уровень выше (Searcher -> Scraper)
        var currentDir = Directory.GetCurrentDirectory();
        var jsonFilePath = "articles.json";
        
        // Сначала пробуем файл в текущей директории
        if (!File.Exists(jsonFilePath))
        {
            // Пробуем в родительской директории (корень проекта)
            jsonFilePath = Path.Combine("..", "articles.json");
        }
        
        if (!File.Exists(jsonFilePath))
        {
            // Пробуем через полный путь относительно текущего расположения exe
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            jsonFilePath = Path.Combine(projectRoot, "articles.json");
        }

        Console.WriteLine($"Индексация статей");
        Console.WriteLine();

        try
        {
            if (!File.Exists(jsonFilePath))
            {
                Console.WriteLine($"Файл articles.json не найден в корне проекта!");
                Console.WriteLine($"Искали в: {Path.GetFullPath(jsonFilePath)}");
                return;
            }

            // Переиспользуем сервис индексации, который уже содержит всю логику разбора JSON
            await indexingService.IndexArticlesFromJsonAsync(jsonFilePath);
            
            // Показываем общее количество документов в индексе после индексации
            var totalDocs = await elasticSearchService.GetTotalDocumentsAsync();
            Console.WriteLine($"Всего документов в индексе: {totalDocs}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при индексации: {ex.Message}");
            if (ex.StackTrace != null)
                Console.WriteLine($"Стек вызовов: {ex.StackTrace}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Обрабатывает команду майнинга синонимов из статей.
    /// </summary>
    /// <param name="command">Строка команды в формате "mine-synonyms [--force] [--threshold=<значение>]"</param>
    /// <param name="synonyms">Провайдер синонимов для сохранения результатов</param>
    static async Task HandleMineSynonymsCommand(string command, SynonymProvider synonyms)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool force = parts.Any(p => p == "--force" || p.ToLower() == "-f");
        
        double? customThreshold = null;
        foreach (var part in parts)
        {
            if (part.StartsWith("--threshold=", StringComparison.OrdinalIgnoreCase))
            {
                var thresholdStr = part.Substring("--threshold=".Length);
                if (double.TryParse(thresholdStr, System.Globalization.NumberStyles.Float, 
                    System.Globalization.CultureInfo.InvariantCulture, out var threshold))
                {
                    customThreshold = threshold;
                }
            }
        }

        // Определяем путь к articles.json
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var jsonFilePath = Path.Combine(projectRoot, "articles.json");

        if (!File.Exists(jsonFilePath))
        {
            jsonFilePath = "articles.json";
        }

        if (!File.Exists(jsonFilePath))
        {
            Console.WriteLine($"Файл articles.json не найден!");
            Console.WriteLine($"Искали в: {Path.GetFullPath(jsonFilePath)}");
            Console.WriteLine("Сначала выполните команду 'scrape <количество>' для сбора статей.");
            return;
        }

        // Определяем путь для сохранения синонимов (всегда в корне проекта)
        var synonymsPath = Path.GetFullPath(Path.Combine(projectRoot, "synonyms.json"));
        
        // Убеждаемся, что директория существует
        var directory = Path.GetDirectoryName(synonymsPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Проверяем, есть ли уже файл синонимов
        if (File.Exists(synonymsPath) && !force)
        {
            Console.WriteLine($"Файл синонимов уже существует: {synonymsPath}");
            Console.WriteLine("Используйте --force для пересчета синонимов.");
            Console.WriteLine();
            
            // Загружаем существующие синонимы
            synonyms.LoadFromFile(synonymsPath);
            Console.WriteLine($"Загружено {synonyms.GroupCount} групп синонимов.");
            return;
        }

        Console.WriteLine("Майнинг синонимов");
        Console.WriteLine($"Анализируем статьи из: {jsonFilePath}");
        Console.WriteLine($"Результаты будут сохранены в: {synonymsPath}");
        if (customThreshold.HasValue)
        {
            Console.WriteLine($"Порог схожести: {customThreshold.Value}");
        }
        Console.WriteLine();

        try
        {
            var miner = new SynonymMiner();
            var options = MiningOptions.CreateDefault();
            
            if (customThreshold.HasValue)
            {
                options.MinSimilarityThreshold = Math.Max(0.0, Math.Min(1.0, customThreshold.Value));
            }

            var synonymData = await miner.MineFromJsonFileAsync(jsonFilePath, options);

            if (synonymData.Synonyms.Count == 0)
            {
                Console.WriteLine("Синонимы не найдены. Попробуйте:");
                Console.WriteLine("  - Снизить порог схожести: mine-synonyms --threshold=0.15");
                Console.WriteLine("  - Убедиться, что в articles.json достаточно статей");
                return;
            }

            // Сохраняем результаты
            synonymData.LastUpdated = DateTime.UtcNow;
            synonyms.SaveToFile(synonymData, synonymsPath);
            synonyms.LoadFromData(synonymData);

            Console.WriteLine();
            Console.WriteLine("Майнинг синонимов завершен");
            Console.WriteLine($"Найдено групп синонимов: {synonymData.TotalGroups}");
            if (synonymData.Statistics != null)
            {
                Console.WriteLine($"Всего пар: {synonymData.Statistics.TotalPairs}");
                Console.WriteLine($"Средняя схожесть: {synonymData.Statistics.AvgSimilarity:F3}");
            }
            Console.WriteLine($"Синонимы сохранены в: {synonymsPath}");
            Console.WriteLine("Теперь они будут автоматически использоваться при поиске.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при майнинге синонимов: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Детали: {ex.InnerException.Message}");
            }
            if (ex.StackTrace != null)
            {
                Console.WriteLine($"Стек вызовов: {ex.StackTrace}");
            }
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Обрабатывает команду создания бэкапа
    /// </summary>
    /// <param name="command">Строка команды в формате "backup [путь]"</param>
    /// <param name="backupService">Сервис для создания бэкапов</param>
    static async Task HandleBackupCommand(string command, ElasticsearchBackupService backupService)
    {
        Console.WriteLine("Создание бэкапа");
        
        try
        {
            var backupPath = await backupService.CreateBackupAsync();
            Console.WriteLine($"Бэкап создан: {backupPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка создания бэкапа: {ex.Message}");
        }
        
        Console.WriteLine();
    }

    /// <summary>
    /// Обрабатывает команду восстановления из бэкапа
    /// </summary>
    /// <param name="command">Строка команды в формате "restore <путь> [--force]"</param>
    /// <param name="backupService">Сервис для восстановления бэкапов</param>
    static async Task HandleRestoreCommand(string command, ElasticsearchBackupService backupService)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var force = parts.Any(p => p == "--force");
        
        try
        {
            Console.WriteLine("Восстановление из папки backup");
            
            // Определяем правильный путь к папке backup
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            var backupPath = Path.Combine(projectRoot, "backup");
            
            Console.WriteLine($"Ищем бэкап в: {backupPath}");
            
            if (!Directory.Exists(backupPath))
            {
                Console.WriteLine($"Папка backup не найдена в: {backupPath}");
                Console.WriteLine($"Текущая директория: {Directory.GetCurrentDirectory()}");
                Console.WriteLine($"BaseDirectory: {baseDir}");
                Console.WriteLine($"ProjectRoot: {projectRoot}");
                
                // Попробуем найти папку backup в других местах
                var alternativePaths = new[]
                {
                    Path.Combine(Directory.GetCurrentDirectory(), "backup"),
                    Path.Combine(Directory.GetCurrentDirectory(), "..", "backup"),
                    "backup"
                };
                
                foreach (var altPath in alternativePaths)
                {
                    var fullAltPath = Path.GetFullPath(altPath);
                    Console.WriteLine($"Проверяем: {fullAltPath}");
                    if (Directory.Exists(fullAltPath))
                    {
                        backupPath = fullAltPath;
                        Console.WriteLine($"Найдена папка backup в: {backupPath}");
                        break;
                    }
                }
            }
            
            await backupService.RestoreBackupAsync(backupPath, force: force);
            Console.WriteLine("Восстановление завершено");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка восстановления: {ex.Message}");
        }
        
        Console.WriteLine();
    }

    /// <summary>
    /// Показывает статистику работы spell checker
    /// </summary>
    static void HandleStatsCommand()
    {
        Console.WriteLine("Статистика Spell Checker");
        Console.WriteLine(new string('=', 40));

        if (CompositeSpellChecker == null)
        {
            Console.WriteLine("Spell checker не инициализирован");
            return;
        }

        var stats = CompositeSpellChecker.GetStats();
        Console.WriteLine($"Активных проверщиков: {stats.CheckersCount}");
        Console.WriteLine($"Кэш: {stats.CacheSize}/{stats.MaxCacheSize}");
        Console.WriteLine();

        Console.WriteLine("Доступные методы:");
        foreach (var checkerName in stats.CheckerNames)
        {
            Console.WriteLine($"  - {checkerName}");
        }

        // Показываем статистику аналитики, если доступна
        var analyticsChecker = CompositeSpellChecker._checkers?.OfType<SearchAnalyticsSpellChecker>().FirstOrDefault();
        if (analyticsChecker != null)
        {
            Console.WriteLine();
            Console.WriteLine("Аналитика поиска:");
            var analyticsStats = analyticsChecker.GetAnalyticsStats();
            Console.WriteLine($"  Уникальных запросов: {analyticsStats.TotalUniqueQueries}");
            Console.WriteLine($"  Всего поисков: {analyticsStats.TotalSearches}");
            Console.WriteLine($"  Успешных запросов: {analyticsStats.SuccessfulQueries}");
            Console.WriteLine($"  Выученных исправлений: {analyticsStats.LearnedCorrections}");
            Console.WriteLine($"  Общий успех: {analyticsStats.SuccessRate:P1}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Обрабатывает команду оценки качества поиска
    /// </summary>
    /// <param name="command">Строка команды в формате "evaluate <запрос> [--ai] [category=<категория>] [author=<автор>]"</param>
    /// <param name="elasticSearchService">Сервис для выполнения поиска</param>
    static async Task HandleEvaluateCommand(string command, ElasticSearchService elasticSearchService)
    {
        var arguments = command.Length > "evaluate".Length
            ? command.Substring("evaluate".Length).Trim()
            : string.Empty;

        if (string.IsNullOrWhiteSpace(arguments))
        {
            Console.WriteLine("Использование: evaluate <запрос> [category=<категория>] [author=<автор>]");
            return;
        }

        // Парсим аргументы (переиспользуем логику из HandleSearchCommand)
        var tokens = SplitArguments(arguments);
        if (tokens.Count == 0)
        {
            Console.WriteLine("Запрос не может быть пустым!");
            return;
        }

        var queryParts = new List<string>();
        var useAi = false;
        string? category = null;
        string? author = null;

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // Флаг --ai включает использование нейросети для ранжирования и подсказок
            if (string.Equals(token, "--ai", StringComparison.OrdinalIgnoreCase))
            {
                useAi = true;
                continue;
            }

            if (TryParseSearchTag(token, out var tagType, out var inlineValue, out var requiresNextValue))
            {
                string? tagValue = inlineValue;

                if (requiresNextValue)
                {
                    if (i + 1 >= tokens.Count)
                    {
                        Console.WriteLine($"Тег '{GetTagLabel(tagType)}' требует значение.");
                        return;
                    }
                    tagValue = tokens[++i];
                }

                if (string.IsNullOrWhiteSpace(tagValue))
                    continue;

                switch (tagType)
                {
                    case SearchTagType.Category:
                        category = TextPreprocessor.NormalizeOrNull(tagValue);
                        break;
                    case SearchTagType.Author:
                        author = TextPreprocessor.NormalizeOrNull(tagValue);
                        break;
                }
                continue;
            }

            var normalizedToken = TextPreprocessor.Normalize(token);
            if (StopWords.IsStopWord(normalizedToken))
                continue;

            if (!string.IsNullOrEmpty(normalizedToken))
                queryParts.Add(normalizedToken);
        }

        var query = string.Join(' ', queryParts).Trim();
        if (string.IsNullOrEmpty(query))
        {
            Console.WriteLine("Запрос не может быть пустым!");
            return;
        }

        // Расширяем запрос синонимами
        var expandedQuery = Synonyms.ExpandQuery(query);
        
        Console.WriteLine($"Выполняется поиск для оценки: '{expandedQuery}'");
        if (!string.IsNullOrEmpty(category))
            Console.WriteLine($"Категория: {category}");
        if (!string.IsNullOrEmpty(author))
            Console.WriteLine($"Автор: {author}");
        Console.WriteLine();

        try
        {
            // Проверяем, что индекс существует
            var totalDocs = await elasticSearchService.GetTotalDocumentsAsync();
            if (totalDocs == 0)
            {
                Console.WriteLine("Индекс пуст или не существует. Сначала выполните команду 'index' или 'scrape <количество>'.");
                Console.WriteLine();
                return;
            }
            
            // Выполняем поиск (берем до 5 результатов для оценки)
            var result = await elasticSearchService.SearchAsync(expandedQuery, 0, 5, category, author);
            
            Console.WriteLine($"Найдено документов: {result.Total}");
            Console.WriteLine($"Показано для оценки: {result.Documents.Count}");
            Console.WriteLine(new string('=', 80));

            // Используем нейросеть для ранжирования, если она включена флагом --ai и доступна
            if (useAi && RelevanceLabeler != null && result.Documents.Count > 1)
            {
                Console.WriteLine("[Нейросеть] Переупорядочивание результатов для оценки...");
                try
                {
                    result.Documents = await RelevanceLabeler.RerankAsync(query, result.Documents);
                    Console.WriteLine("[Нейросеть] Для оценки используется порядок после переупорядочивания на основе нейросети.");
                    Console.WriteLine(new string('=', 80));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Нейросеть] Ошибка ранжирования: {ex.Message}");
                    Console.WriteLine("[Нейросеть] Используется исходный порядок результатов.");
                    Console.WriteLine(new string('=', 80));
                }
            }
            // Если нейросеть недоступна или не включена, используем ML.NET reranker
            else if (Reranker?.IsReady == true && result.Documents.Count > 1)
            {
                result.Documents = Reranker.Rerank(query, result.Documents);
                Console.WriteLine("[Reranker] Для оценки используется порядок после ML-переупорядочивания.");
                Console.WriteLine(new string('=', 80));
            }

            var evaluation = new QueryEvaluation
            {
                QueryId = Guid.NewGuid().ToString(),
                QueryText = query,
                Timestamp = DateTime.UtcNow,
                Category = category,
                Author = author,
                TotalFound = result.Total
            };

            // Показываем результаты и собираем оценки
            for (int i = 0; i < result.Documents.Count; i++)
            {
                var doc = result.Documents[i];
                Console.WriteLine($"{i + 1}. {doc.Title}");
                Console.WriteLine($"   URL: {doc.Url}");
                if (!string.IsNullOrEmpty(doc.Category))
                    Console.WriteLine($"   Категория: {doc.Category}");
                if (!string.IsNullOrEmpty(doc.Author))
                    Console.WriteLine($"   Автор: {doc.Author}");
                if (doc.PublishDate.HasValue)
                    Console.WriteLine($"   Дата: {doc.PublishDate.Value:yyyy-MM-dd HH:mm}");

                // Показываем фрагмент содержимого
                if (!string.IsNullOrEmpty(doc.Content))
                {
                    var preview = doc.Content.Length > 200 
                        ? doc.Content.Substring(0, 200) + " [обрезано]" 
                        : doc.Content;
                    Console.WriteLine($"   Содержание: {preview}");
                }

                Console.WriteLine();

                // Получаем оценку от нейросети, если доступна
                int? aiRelevanceScore = null;
                float? aiConfidence = null;
                string? aiReason = null;
                if (useAi && RelevanceLabeler != null)
                {
                    Console.WriteLine("[Нейросеть] Оценка релевантности...");
                    try
                    {
                        var prediction = await RelevanceLabeler.EvaluateAsync(query, doc);
                        if (prediction.IsSuccess)
                        {
                            aiRelevanceScore = prediction.Label;
                            aiConfidence = prediction.Confidence;
                            aiReason = prediction.Reason;
                            var scoreLabel = aiRelevanceScore switch
                            {
                                0 => "Нерелевантно",
                                1 => "Частично релевантно",
                                2 => "Очень релевантно",
                                _ => "Неизвестно"
                            };
                            Console.WriteLine($"[Нейросеть] Оценка: {aiRelevanceScore} ({scoreLabel})");
                            Console.WriteLine($"[Нейросеть] Уверенность: {prediction.Confidence:P1}");
                            if (!string.IsNullOrWhiteSpace(aiReason))
                            {
                                Console.WriteLine($"[Нейросеть] Обоснование: {aiReason}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[Нейросеть] Ошибка оценки: {prediction.ErrorMessage}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Нейросеть] Ошибка при оценке: {ex.Message}");
                    }
                    Console.WriteLine();
                }

                Console.WriteLine("Оцените релевантность этого результата:");
                Console.WriteLine("  0 - Нерелевантно (не подходит к запросу)");
                Console.WriteLine("  1 - Частично релевантно (подходит, но не очень)");
                Console.WriteLine("  2 - Очень релевантно (отлично подходит)");
                if (aiRelevanceScore.HasValue)
                {
                    Console.WriteLine($"  [Подсказка: нейросеть оценила как {aiRelevanceScore.Value}]");
                }
                Console.Write("Ваша оценка (0-2): ");

                int relevanceScore = 0;
                var input = Console.ReadLine();
                if (int.TryParse(input, out var score) && score >= 0 && score <= 2)
                {
                    relevanceScore = score;
                }
                else
                {
                    Console.WriteLine("Некорректная оценка, используется 0 (нерелевантно)");
                }

                // Опциональный комментарий
                Console.Write("Комментарий (необязательно): ");
                var comment = Console.ReadLine();

                evaluation.Results.Add(new ResultRelevance
                {
                    Position = i + 1,
                    DocumentId = doc.Id,
                    Title = doc.Title,
                    Url = doc.Url,
                    Category = doc.Category,
                    Author = doc.Author,
                    RelevanceScore = relevanceScore,
                    Comment = string.IsNullOrWhiteSpace(comment) ? null : comment,
                    AiRelevanceScore = aiRelevanceScore,
                    AiConfidence = aiConfidence,
                    AiReason = aiReason
                });

                Console.WriteLine(new string('-', 80));
            }

            // Сохраняем оценку
            var evaluationService = new EvaluationService();
            
            Console.WriteLine("Сохранение оценки");
            try 
            {
                await evaluationService.SaveQueryEvaluationAsync(evaluation);
                Console.WriteLine("Оценка успешно сохранена");
            }
            catch (Exception saveEx)
            {
                Console.WriteLine($"Ошибка при сохранении оценки: {saveEx.Message}");
                Console.WriteLine($"Путь к файлу: {Path.GetFullPath("evaluation_data.json")}");
            }

            // Рассчитываем и показываем метрики
            var calculator = new MetricsCalculator();
            var metrics = calculator.CalculateMetrics(evaluation);

            Console.WriteLine();
            Console.WriteLine("Метрики для этого запроса:");
            Console.WriteLine($"  Precision@1:  {metrics.PrecisionAt1:F3}");
            Console.WriteLine($"  Precision@5:  {metrics.PrecisionAt5:F3}");
            Console.WriteLine($"  Precision@10: {metrics.PrecisionAt10:F3}");
            Console.WriteLine($"  Average Precision: {metrics.AveragePrecision:F3}");
            Console.WriteLine($"  NDCG: {metrics.NDCG:F3}");
            Console.WriteLine($"  Reciprocal Rank: {metrics.ReciprocalRank:F3}");
            Console.WriteLine($"  Релевантных документов: {metrics.RelevantCount} из {metrics.TotalCount}");
            
            Console.WriteLine();
            Console.WriteLine($"Оценка сохранена с ID: {evaluation.QueryId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при выполнении оценки: {ex.Message}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Показывает все рассчитанные метрики качества поиска
    /// </summary>
    static async Task HandleShowMetricsCommand()
    {
        var evaluationService = new EvaluationService();
        var evaluations = await evaluationService.LoadAllEvaluationsAsync();

        if (evaluations.Count == 0)
        {
            Console.WriteLine("Нет данных для оценки. Сначала выполните команду 'evaluate <запрос>'.");
            Console.WriteLine();
            return;
        }

        var reportGenerator = new EvaluationReportGenerator();
        var fullReport = reportGenerator.GenerateFullReport(evaluations);
        
        Console.WriteLine(fullReport);
        
        // Предложение сохранить отчет
        Console.Write("Сохранить отчет в файл? (y/N): ");
        var saveResponse = Console.ReadLine();
        
        if (saveResponse?.ToLower() == "y" || saveResponse?.ToLower() == "yes")
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var reportPath = $"evaluation_report_{timestamp}.txt";
            
            try
            {
                await reportGenerator.SaveReportToFileAsync(fullReport, reportPath);
                Console.WriteLine($"Отчет сохранен в файл: {reportPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при сохранении отчета: {ex.Message}");
            }
        }
        
        Console.WriteLine();
    }

    /// <summary>
    /// Сохраняет полный отчет в текстовый файл
    /// </summary>
    /// <param name="command">Команда с путем к файлу</param>
    static async Task HandleSaveReportCommand(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            Console.WriteLine("Использование: save-report <файл.txt>");
            return;
        }

        var reportPath = parts[1];
        if (!reportPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            reportPath += ".txt";
        }

        try
        {
            var evaluationService = new EvaluationService();
            var evaluations = await evaluationService.LoadAllEvaluationsAsync();

            if (evaluations.Count == 0)
            {
                Console.WriteLine("Нет данных для создания отчета. Сначала выполните оценку запросов.");
                return;
            }

            var reportGenerator = new EvaluationReportGenerator();
            var fullReport = reportGenerator.GenerateFullReport(evaluations);
            
            await reportGenerator.SaveReportToFileAsync(fullReport, reportPath);
            Console.WriteLine($"Полный отчет сохранен в файл: {reportPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при сохранении отчета: {ex.Message}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Экспортирует данные оценки в CSV файл
    /// </summary>
    /// <param name="command">Команда с путем к файлу</param>
    static async Task HandleExportEvaluationCommand(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            Console.WriteLine("Использование: export-evaluation <файл.csv>");
            return;
        }

        var csvPath = parts[1];
        if (!csvPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            csvPath += ".csv";
        }

        try
        {
            var evaluationService = new EvaluationService();
            var fullCsvPath = Path.GetFullPath(csvPath);
            Console.WriteLine($"Экспорт в файл: {fullCsvPath}");
            
            await evaluationService.ExportToCsvAsync(fullCsvPath);
            Console.WriteLine($"Данные оценки экспортированы в файл: {fullCsvPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при экспорте: {ex.Message}");
            Console.WriteLine($"Стек вызовов: {ex.StackTrace}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Сбор 5k запросов и автоматическая разметка релевантности через Qwen
    /// </summary>
    static async Task HandleBuildRerankerCommand(string command, ElasticSearchService elasticSearchService)
    {
        var arguments = command.Length > "build-reranker".Length
            ? command.Substring("build-reranker".Length).Trim()
            : string.Empty;
        var options = ParseOptionDictionary(SplitArguments(arguments));

        int queryCount = GetIntOption(options, "queries", 5000);
        int docsPerQuery = GetIntOption(options, "docs", 20);
        string? datasetPath = GetOption(options, "dataset");
        string? articlesPath = GetOption(options, "articles");
        string? ollamaUrl = GetOption(options, "ollama");
        string? modelName = GetOption(options, "model");

        var generator = new QueryGenerator(articlesPath);
        var labeler = await QwenRelevanceLabeler.CreateAsync(ollamaUrl, modelName);
        if (labeler == null)
        {
            Console.WriteLine("Qwen недоступен. Проверьте Ollama и параметры подключения.");
            return;
        }

        using (labeler)
        {
            var builder = new RerankerDatasetBuilder(elasticSearchService, generator, labeler, datasetPath);
            try
            {
                var report = await builder.BuildAsync(queryCount, docsPerQuery);
                Console.WriteLine($"[Dataset] Добавлено запросов: {report.GeneratedQueries}, пар: {report.GeneratedPairs}");
                Console.WriteLine($"[Dataset] Всего запросов: {report.TotalQueries}, пар: {report.TotalPairs}");
                Console.WriteLine($"[Dataset] Файл: {builder.DatasetPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка сбора датасета: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Обучение ML reranker модели и подключение её к поиску
    /// </summary>
    static async Task HandleTrainRerankerCommand(string command)
    {
        var arguments = command.Length > "train-reranker".Length
            ? command.Substring("train-reranker".Length).Trim()
            : string.Empty;
        var options = ParseOptionDictionary(SplitArguments(arguments));

        var datasetPath = GetOption(options, "dataset") 
            ?? Path.Combine(GetProjectRoot(), "data", "reranker", "dataset.jsonl");
        var modelPath = GetOption(options, "output");
        var testFraction = GetDoubleOption(options, "test", 0.2);

        try
        {
            var trainer = new RerankerTrainer(datasetPath, modelPath);
            var report = await trainer.TrainAsync(testFraction);

            Console.WriteLine("=== Итоги обучения reranker ===");
            Console.WriteLine($"Документо-записей: {report.TotalPairs}");
            Console.WriteLine($"Уникальных запросов: {report.TotalQueries}");
            Console.WriteLine($"NDCG@3: {report.MeanNdcgAt3:F3}");
            Console.WriteLine($"NDCG@10: {report.MeanNdcgAt10:F3}");
            Console.WriteLine($"Модель сохранена: {report.ModelPath}");

            Reranker?.Dispose();
            Reranker = new RerankerService(report.ModelPath);
            if (Reranker.TryLoad())
            {
                Console.WriteLine("Реранкер перезагружен и готов к работе.");
            }
            else
            {
                Console.WriteLine("Не удалось загрузить новую модель для поиска.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка обучения reranker: {ex.Message}");
        }
    }

    /// <summary>
    /// Подготовка датасета для fine-tuning нейросети из оценок пользователей
    /// </summary>
    static async Task HandleBuildFineTuningCommand(string command, ElasticSearchService elasticSearchService)
    {
        var arguments = command.Length > "build-finetuning".Length
            ? command.Substring("build-finetuning".Length).Trim()
            : string.Empty;
        var options = ParseOptionDictionary(SplitArguments(arguments));

        string? datasetPath = GetOption(options, "dataset");
        int minRelevanceScore = GetIntOption(options, "min-score", 0);

        Console.WriteLine("=== Подготовка датасета для fine-tuning нейросети ===");
        Console.WriteLine($"Минимальная оценка релевантности: {minRelevanceScore}");
        Console.WriteLine();

        try
        {
            var evaluationService = new EvaluationService();
            var builder = new FineTuningDatasetBuilder(evaluationService, elasticSearchService, datasetPath);

            var report = await builder.BuildFromEvaluationsAsync(minRelevanceScore);

            Console.WriteLine();
            Console.WriteLine("=== Итоги подготовки датасета ===");
            Console.WriteLine($"Всего примеров обработано: {report.TotalExamples}");
            Console.WriteLine($"Успешно создано: {report.SuccessfulExamples}");
            Console.WriteLine($"Ошибок: {report.FailedExamples}");
            Console.WriteLine($"Файл датасета: {builder.DatasetPath}");
            Console.WriteLine();
            Console.WriteLine("Датасет готов для fine-tuning модели Qwen/Ollama.");
            Console.WriteLine("Используйте этот файл для обучения кастомной модели.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка подготовки датасета: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Детали: {ex.InnerException.Message}");
            }
        }
    }

    /// <summary>
    /// Очищает все данные оценки
    /// </summary>
    static async Task HandleClearEvaluationCommand()
    {
        Console.Write("Вы уверены, что хотите удалить все данные оценки? (y/N): ");
        var confirmation = Console.ReadLine();

        if (confirmation?.ToLower() == "y" || confirmation?.ToLower() == "yes")
        {
            try
            {
                var evaluationService = new EvaluationService();
                
                // Создаем резервную копию перед удалением
                var stats = await evaluationService.GetBasicStatsAsync();
                if (stats.TotalQueries > 0)
                {
                    var backupPath = await evaluationService.CreateBackupAsync();
                    Console.WriteLine($"Создана резервная копия: {backupPath}");
                }

                await evaluationService.ClearAllEvaluationsAsync();
                Console.WriteLine("Все данные оценки удалены.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при очистке данных: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("Операция отменена.");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Отображает главное меню с доступными командами
    /// </summary>
    static void ShowMainMenu()
    {
        Console.WriteLine("ПОИСКОВИК СТАТЕЙ");
        Console.WriteLine(new string('=', 50));
        Console.WriteLine();
        Console.WriteLine("ОСНОВНЫЕ КОМАНДЫ:");
        Console.WriteLine("  scrape <количество> [--clear] - скрапить статьи и проиндексировать");
        Console.WriteLine("  index - проиндексировать статьи из articles.json");
        Console.WriteLine("  search <запрос> [теги] - поиск по статьям");
        Console.WriteLine();
        Console.WriteLine("ОЦЕНКА КАЧЕСТВА:");
        Console.WriteLine("  evaluate <запрос> [--ai] - выполнить поиск и оценить результаты (с нейросетью по флагу)");
        Console.WriteLine("  show-metrics - показать метрики качества поиска");
        Console.WriteLine("  save-report <файл.txt> - сохранить отчет в файл");
        Console.WriteLine("  export-evaluation <файл.csv> - экспорт данных в CSV");
        Console.WriteLine("  build-reranker [--queries --docs] - собрать датасет через Qwen");
        Console.WriteLine("  train-reranker [--dataset --output] - обучить reranker и подключить его");
        Console.WriteLine();
        Console.WriteLine("УПРАВЛЕНИЕ ДАННЫМИ:");
        Console.WriteLine("  backup - создать бэкап индекса");
        Console.WriteLine("  restore [--force] - восстановить индекс из бэкапа");
        Console.WriteLine("  clear-evaluation - очистить данные оценки");
        Console.WriteLine();
        Console.WriteLine("ДОПОЛНИТЕЛЬНО:");
        Console.WriteLine("  mine-synonyms [--force] - автоматический майнинг синонимов");
        Console.WriteLine("  stats - показать статистику spell checker");
        Console.WriteLine("  clear - очистить консоль и показать это меню");
        Console.WriteLine("  exit - выход из программы");
        Console.WriteLine();
        Console.WriteLine("ПРИМЕРЫ ИСПОЛЬЗОВАНИЯ:");
        Console.WriteLine("  > search политика");
        Console.WriteLine("  > evaluate спорт");
        Console.WriteLine("  > show-metrics");
        Console.WriteLine();
    }

    /// <summary>
    /// Обрабатывает команду очистки консоли
    /// </summary>
    static void HandleClearCommand()
    {
        ClearConsole();
        ShowMainMenu();
    }

    /// <summary>
    /// Очищает консоль различными способами в зависимости от среды выполнения
    /// </summary>
    static void ClearConsole()
    {
        try
        {
            // Метод 1: Стандартная очистка консоли
            Console.Clear();
        }
        catch
        {
            try
            {
                // Метод 2: ANSI escape sequences (работает в большинстве современных терминалов)
                Console.Write("\u001b[2J\u001b[H");
            }
            catch
            {
                // Метод 3: Если ничего не работает, выводим много пустых строк
                Console.WriteLine(new string('\n', 50));
            }
        }
    }
}





