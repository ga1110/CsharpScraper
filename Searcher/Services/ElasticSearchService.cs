using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Elastic.Transport;
using Scraper.Models;
using Searcher.Models;
using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Searcher.Services;

/// <summary>
/// Сервис для работы с ElasticSearch: создание индекса, индексация статей и поиск с поддержкой фильтров
/// </summary>
public class ElasticSearchService
{
    private readonly ElasticsearchClient _client;
    private const string _indexName = "articles";
    private static readonly string _connectionString =
        Environment.GetEnvironmentVariable("ELASTICSEARCH_URL") ?? "https://localhost:9200";
    /// <summary>
    /// Инициализирует новый экземпляр ElasticSearchService с указанным адресом ElasticSearch сервера
    /// </summary>
    /// <param name="_connectionString">Строка подключения к ElasticSearch (по умолчанию "http://localhost:9200")</param>
    /// <param name="username">Имя пользователя для базовой аутентификации (опционально)</param>
    /// <param name="password">Пароль для базовой аутентификации (опционально)</param>
    public ElasticSearchService(string? username = null, string? password = null)
    {
        var uri = new Uri(_connectionString);
        var settings = new ElasticsearchClientSettings(uri)
            .DefaultIndex(_indexName)
            .EnableDebugMode();

        // Настраиваем SSL для HTTPS подключений
        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            // В учебных проектах часто используются self-signed сертификаты, поэтому отключаем проверку
            settings.ServerCertificateValidationCallback((sender, certificate, chain, sslPolicyErrors) => true);

        // Настраиваем аутентификацию через базовую аутентификацию, если указаны username и password
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            settings.Authentication(new BasicAuthentication(username, password));

        // Создаём клиент ElasticSearch, который будем переиспользовать для всех операций
        _client = new ElasticsearchClient(settings);
    }

    /// <summary>
    /// Проверяет существование индекса и создает его с настройками маппинга, если индекс не существует
    /// </summary>
    /// <returns>true, если индекс существует или был успешно создан, иначе false</returns>
    public async Task<bool> EnsureIndexExistsAsync()
    {
        try
        {
            // Проверяем, существует ли индекс, чтобы не создавать его повторно
            var existsResponse = await _client.Indices.ExistsAsync(_indexName);
            
            if (!existsResponse.Exists)
            {
                // Описываем схему индекса: типы полей, ключевые слова, текстовые поля и т.д.
                var createResponse = await _client.Indices.CreateAsync(_indexName, c => c
                    .Mappings(m => m
                        .Properties<ArticleDocument>(p => p
                            .Keyword(k => k.Id)
                            .Text(t => t.Title)
                            .Keyword(k => k.Url)
                            .Text(t => t.Content)
                            .Keyword(k => k.Category)
                            .Date(d => d.PublishDate)
                            .Keyword(k => k.Author)
                            .IntegerNumber(n => n.CommentCount)
                            .Keyword(k => k.ImageUrl)
                        )
                    )
                );

                if (!createResponse.IsValidResponse)
                {
                    if (createResponse.ElasticsearchServerError != null)
                    {
                        var error = createResponse.ElasticsearchServerError.Error;
                        Console.WriteLine($"Ошибка при создании индекса:");
                        Console.WriteLine($"Ошибка сервера: {error?.Reason}");
                        if (error?.RootCause != null && error.RootCause.Count > 0)
                        {
                            foreach (var cause in error.RootCause)
                                Console.WriteLine($"  Причина: {cause.Reason}");
                        }
                    }
                    else
                        Console.WriteLine($"Детали: {createResponse.DebugInformation}");
                    return false;
                }

                return true;
            }

            return true;
        }
        catch (Exception ex)
        {
            if (ex is System.Net.Sockets.SocketException || 
                ex.InnerException is System.Net.Sockets.SocketException ||
                ex.Message.Contains("отверг запрос") ||
                ex.Message.Contains("connection refused"))
            {
                Console.WriteLine("Ошибка подключения к ElasticSearch!");
                Console.WriteLine("Убедитесь, что ElasticSearch запущен и доступен");
                Console.WriteLine($"Детали: {ex.Message}");
                return false;
            }
            
            Console.WriteLine($"Исключение при работе с индексом: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"Внутренняя ошибка: {ex.InnerException.Message}");
            return false;
        }
    }

    /// <summary>
    /// Индексирует одну статью в ElasticSearch, используя SHA256 хеш URL в качестве идентификатора документа
    /// </summary>
    /// <param name="article">Статья для индексации</param>
    /// <returns>true, если индексация прошла успешно, иначе false</returns>
    public async Task<bool> IndexArticleAsync(Article article)
    {
        // Преобразуем модель скрапера в документ, подходящий для ElasticSearch
        var document = new ArticleDocument
        {
            Id = GenerateArticleId(article.Url),
            Title = article.Title,
            Url = article.Url,
            Content = article.Content,
            Category = article.Category,
            PublishDate = article.PublishDate,
            Author = article.Author,
            CommentCount = article.CommentCount,
            ImageUrl = article.ImageUrl
        };

        var response = await _client.IndexAsync(document, _indexName, document.Id);

        // Возвращаем флаг успешности, чтобы вызывающий код мог отреагировать на ошибки
        return response.IsValidResponse;
    }

    /// <summary>
    /// Генерирует уникальный ID для статьи на основе URL
    /// </summary>
    private string GenerateArticleId(string url)
    {
        using var sha256 = SHA256.Create();
        // Используем SHA256 от URL, чтобы одинаковые статьи всегда получали одинаковый ID
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(url));
        return Convert.ToBase64String(hashBytes).Replace("/", "_").Replace("+", "-").TrimEnd('=');
    }

    /// <summary>
    /// Индексирует список статей в ElasticSearch пакетной операцией для повышения производительности
    /// </summary>
    /// <param name="articles">Список статей для индексации</param>
    /// <returns>Кортеж: (успешность операции, количество фактически проиндексированных документов, количество удаленных дубликатов)</returns>
    public async Task<(bool Success, int IndexedCount)> IndexArticlesAsync(List<Article> articles)
    {
        // Сначала проектируем список документов с предсказуемыми ID и нужными полями
        var documents = articles.Select(article => new ArticleDocument
        {
            Id = GenerateArticleId(article.Url),
            Title = article.Title,
            Url = article.Url,
            Content = article.Content,
            Category = article.Category,
            PublishDate = article.PublishDate,
            Author = article.Author,
            CommentCount = article.CommentCount,
            ImageUrl = article.ImageUrl
        }).ToList();

        // Упрощенный подход: индексируем документы пакетами используя IndexAsync
        const int batchSize = 100;
        bool allSuccess = true;
        int successCount = 0;
        var failedArticles = new List<(string Title, string Url, string Error)>();

        for (int i = 0; i < documents.Count; i += batchSize)
        {
            var batch = documents.Skip(i).Take(batchSize).ToList();
            
            // Индексируем каждый документ из батча
            foreach (var doc in batch)
            {
                try
                {
                    // Каждый документ отправляем отдельным запросом — так проще обрабатывать ошибки на учебном проекте
                    var response = await _client.IndexAsync(doc, _indexName, doc.Id);
                    if (response.IsValidResponse)
                        successCount++;
                    else
                    {
                        allSuccess = false;
                        string errorMessage = "Неизвестная ошибка";
                        
                        if (response.ElasticsearchServerError != null)
                            errorMessage = response.ElasticsearchServerError.Error?.Reason ?? "Ошибка сервера ElasticSearch";
                        else if (!string.IsNullOrEmpty(response.DebugInformation))
                            errorMessage = response.DebugInformation;
                        
                        failedArticles.Add((doc.Title, doc.Url, errorMessage));
                        Console.WriteLine($"Ошибка при индексации: \"{doc.Title}\"");
                        Console.WriteLine($"   URL: {doc.Url}");
                        Console.WriteLine($"   Причина: {errorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    allSuccess = false;
                    failedArticles.Add((doc.Title, doc.Url, ex.Message));
                    Console.WriteLine($"Исключение при индексации: \"{doc.Title}\"");
                    Console.WriteLine($"   URL: {doc.Url}");
                    Console.WriteLine($"   Ошибка: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine($"   Внутренняя ошибка: {ex.InnerException.Message}");
                    }
                }
            }
        }


        // Выводим итоговую статистику
        Console.WriteLine();
        Console.WriteLine($"Статистика индексации:");
        Console.WriteLine($"    Успешно проиндексировано: {successCount} из {documents.Count}");
        Console.WriteLine($"    Не удалось: {failedArticles.Count} из {documents.Count}");
        
        if (failedArticles.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Список статей, которые не удалось проиндексировать:");
            for (int i = 0; i < failedArticles.Count; i++)
            {
                var failed = failedArticles[i];
                Console.WriteLine($"   {i + 1}. \"{failed.Title}\"");
                Console.WriteLine($"      URL: {failed.Url}");
                Console.WriteLine($"      Ошибка: {failed.Error}");
            }
        }

        return (allSuccess, successCount);
    }

    /// <summary>
    /// Выполняет полнотекстовый поиск статей с поддержкой фильтров по категории и автору, возвращая результаты с подсветкой совпадений
    /// </summary>
    /// <param name="query">Поисковый запрос для полнотекстового поиска в заголовке, содержимом, авторе и категории</param>
    /// <param name="from">Смещение для пагинации (по умолчанию 0)</param>
    /// <param name="size">Количество результатов для возврата (по умолчанию 10)</param>
    /// <param name="category">Фильтр по категории (опционально)</param>
    /// <param name="author">Фильтр по автору (опционально)</param>
    /// <returns>Результат поиска с документами, общим количеством и подсветкой совпадений</returns>
    public async Task<SearchResult<ArticleDocument>> SearchAsync(
        string query, 
        int from = 0, 
        int size = 10,
        string? category = null,
        string? author = null)
    {
        // MultiMatch позволяет искать одновременно по нескольким полям с разными весами
        Query queryContainer = new MultiMatchQuery
        {
            Query = query,
            Fields = new[] { "title^3", "content", "author", "category" },
            Type = TextQueryType.BestFields,
            Fuzziness = new Fuzziness("AUTO")
        };

        // Добавляем фильтры
        if (!string.IsNullOrEmpty(category) || !string.IsNullOrEmpty(author))
        {
            var filters = new List<Query>();
            
            if (!string.IsNullOrEmpty(category))
            {
                // Фильтр по категории делаем через TermQuery, чтобы искать точные совпадения
                filters.Add(new TermQuery
                {
                    Field = "category",
                    Value = category
                });
            }

            if (!string.IsNullOrEmpty(author))
            {
                filters.Add(new TermQuery
                {
                    Field = "author",
                    Value = author
                });
            }

            if (filters.Count > 0)
            {
                queryContainer = new BoolQuery
                {
                    // Основной текстовый запрос кладём в Must, а фильтры — в Filter (они не влияют на scoring)
                    Must = new[] { queryContainer },
                    Filter = filters
                };
            }
        }

        // Упрощенный запрос без подсветки для совместимости
        var searchRequest = new SearchRequest(_indexName)
        {
            From = from,
            Size = size,
            Query = queryContainer
        };

        var response = await _client.SearchAsync<ArticleDocument>(searchRequest);

        if (!response.IsValidResponse)
        {
            throw new Exception($"Ошибка поиска: {response.DebugInformation}");
        }

        var highlights = new Dictionary<string, List<string>>();
        
        // Обрабатываем подсветку, если она есть в ответе
        if (response.Hits != null)
        {
            foreach (var hit in response.Hits)
            {
                if (hit.Highlight != null && hit.Id != null && hit.Highlight.Count > 0)
                {
                    var highlightList = new List<string>();
                    foreach (var highlightField in hit.Highlight)
                    {
                        if (highlightField.Value != null && highlightField.Value.Count > 0)
                        {
                            highlightList.AddRange(highlightField.Value);
                        }
                    }
                    if (highlightList.Count > 0)
                    {
                        // Сохраняем подсветки по ID документа, чтобы позже быстро найти их при выводе результатов
                        highlights[hit.Id] = highlightList;
                    }
                }
            }
        }

        return new SearchResult<ArticleDocument>
        {
            Documents = response.Documents.ToList(),
            Total = response.Total,
            Highlights = highlights
        };
    }

    /// <summary>
    /// Проверяет подключение к ElasticSearch, выполняя простой ping запрос
    /// </summary>
    /// <returns>true, если подключение успешно, иначе false</returns>
    public async Task<bool> PingAsync()
    {
        try
        {
            // Метод Ping делает HEAD-запрос к корню сервера и возвращает true при HTTP 200
            var response = await _client.PingAsync();
            return response.IsValidResponse;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Получает общее количество документов в индексе статей
    /// </summary>
    /// <returns>Количество документов в индексе</returns>
    public async Task<long> GetTotalDocumentsAsync()
    {
        try
        {
            // Сначала проверяем, существует ли индекс
            var existsResponse = await _client.Indices.ExistsAsync(_indexName);
            if (!existsResponse.Exists)
            {
                // Индекс не существует, возвращаем 0
                return 0;
            }
            
            // Count API быстрее, чем полнотекстовый поиск, и идеально подходит для статистики
            var response = await _client.CountAsync<ArticleDocument>(c => c
                .Indices(_indexName)
            );
            
            if (!response.IsValidResponse)
            {
                if (response.ElasticsearchServerError != null)
                {
                    throw new Exception($"Ошибка ElasticSearch: {response.ElasticsearchServerError.Error?.Reason}");
                }
                throw new Exception($"Невалидный ответ от ElasticSearch: {response.DebugInformation}");
            }
            return response.Count;
        }
        catch
        {
            // Пробрасываем исключение выше для обработки
            throw;
        }
    }

    /// <summary>
    /// Удаляет индекс статей, если он существует
    /// </summary>
    /// <returns>true, если индекс был успешно удален или не существовал, иначе false</returns>
    public async Task<bool> DeleteIndexAsync()
    {
        try
        {
            // Удаляем индекс только если он действительно существует
            var existsResponse = await _client.Indices.ExistsAsync(_indexName);
            
            if (existsResponse.Exists)
            {
                var deleteResponse = await _client.Indices.DeleteAsync(_indexName);
                
                if (!deleteResponse.IsValidResponse)
                {
                    if (deleteResponse.ElasticsearchServerError != null)
                    {
                        var error = deleteResponse.ElasticsearchServerError.Error;
                        Console.WriteLine($"Ошибка при удалении индекса:");
                        Console.WriteLine($"Ошибка сервера: {error?.Reason}");
                        if (error?.RootCause != null && error.RootCause.Count > 0)
                        {
                            foreach (var cause in error.RootCause)
                            {
                                Console.WriteLine($"  Причина: {cause.Reason}");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Детали: {deleteResponse.DebugInformation}");
                    }
                    return false;
                }
                
                return true;
            }
            
            // Индекс не существовал, считаем это успехом
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Исключение при удалении индекса: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Внутренняя ошибка: {ex.InnerException.Message}");
            }
            return false;
        }
    }
}

/// <summary>
/// Класс для представления результатов поиска в ElasticSearch
/// </summary>
/// <typeparam name="T">Тип документов в результатах поиска</typeparam>
public class SearchResult<T>
{
    /// <summary>
    /// Список найденных документов
    /// </summary>
    public List<T> Documents { get; set; } = new();
    
    /// <summary>
    /// Общее количество найденных документов (без учета пагинации)
    /// </summary>
    public long Total { get; set; }
    
    /// <summary>
    /// Словарь подсветок совпадений, где ключ - ID документа, значение - список выделенных фрагментов
    /// </summary>
    public Dictionary<string, List<string>> Highlights { get; set; } = new();
}
