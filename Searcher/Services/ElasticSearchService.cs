using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Elastic.Transport;
using Scraper.Models;
using Searcher.Models;
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
    private const string IndexName = "articles";

    /// <summary>
    /// Инициализирует новый экземпляр ElasticSearchService с указанным адресом ElasticSearch сервера
    /// </summary>
    /// <param name="connectionString">Строка подключения к ElasticSearch (по умолчанию "http://localhost:9200")</param>
    /// <param name="username">Имя пользователя для базовой аутентификации (опционально)</param>
    /// <param name="password">Пароль для базовой аутентификации (опционально)</param>
    public ElasticSearchService(string connectionString = "http://localhost:9200", string? username = null, string? password = null)
    {
        var uri = new Uri(connectionString);
        var settings = new ElasticsearchClientSettings(uri)
            .DefaultIndex(IndexName)
            .EnableDebugMode();

        // Настраиваем SSL для HTTPS подключений (игнорируем проверку сертификата для разработки)
        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            settings.ServerCertificateValidationCallback((sender, certificate, chain, sslPolicyErrors) => true);
        }

        // Настраиваем аутентификацию через базовую аутентификацию, если указаны username и password
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            settings.Authentication(new BasicAuthentication(username, password));
        }

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
            var existsResponse = await _client.Indices.ExistsAsync(IndexName);
            
            if (!existsResponse.Exists)
            {
                var createResponse = await _client.Indices.CreateAsync(IndexName, c => c
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
                            {
                                Console.WriteLine($"  Причина: {cause.Reason}");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Детали: {createResponse.DebugInformation}");
                    }
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
            {
                Console.WriteLine($"Внутренняя ошибка: {ex.InnerException.Message}");
            }
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

        var response = await _client.IndexAsync(document, IndexName, document.Id);

        return response.IsValidResponse;
    }

    /// <summary>
    /// Генерирует уникальный ID для статьи на основе URL
    /// </summary>
    private string GenerateArticleId(string url)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(url));
        return Convert.ToBase64String(hashBytes).Replace("/", "_").Replace("+", "-").TrimEnd('=');
    }

    /// <summary>
    /// Индексирует список статей в ElasticSearch пакетной операцией для повышения производительности
    /// </summary>
    /// <param name="articles">Список статей для индексации</param>
    /// <returns>Кортеж: (успешность операции, количество фактически проиндексированных документов, количество удаленных дубликатов)</returns>
    public async Task<(bool Success, int IndexedCount, int DuplicatesRemoved)> IndexArticlesAsync(List<Article> articles)
    {
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

        // Проверяем на дубликаты ID (могут возникнуть из-за коллизий хеша или одинаковых URL)
        var duplicateIds = documents
            .GroupBy(d => d.Id)
            .Where(g => g.Count() > 1)
            .ToList();
        
        if (duplicateIds.Any())
        {
            Console.WriteLine();
            Console.WriteLine($"⚠️  Обнаружено дубликатов ID: {duplicateIds.Count} уникальных ID встречаются несколько раз");
            Console.WriteLine($"   Это приведет к перезаписи документов при индексации!");
            Console.WriteLine();
            Console.WriteLine("Примеры дубликатов ID:");
            foreach (var group in duplicateIds.Take(5))
            {
                Console.WriteLine($"   ID: {group.Key}");
                foreach (var doc in group.Take(3))
                {
                    Console.WriteLine($"      - \"{doc.Title}\"");
                    Console.WriteLine($"        URL: {doc.Url}");
                }
                if (group.Count() > 3)
                {
                    Console.WriteLine($"      ... и еще {group.Count() - 3}");
                }
            }
            Console.WriteLine();
            Console.WriteLine("Удаляем дубликаты, оставляя только первый документ для каждого ID...");
            
            // Удаляем дубликаты по ID, оставляя первый документ
            documents = documents
                .GroupBy(d => d.Id)
                .Select(g => g.First())
                .ToList();
            
            Console.WriteLine($"После удаления дубликатов ID: {documents.Count} уникальных документов для индексации");
            Console.WriteLine();
        }

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
                    var response = await _client.IndexAsync(doc, IndexName, doc.Id);
                    if (response.IsValidResponse)
                    {
                        successCount++;
                    }
                    else
                    {
                        allSuccess = false;
                        string errorMessage = "Неизвестная ошибка";
                        
                        if (response.ElasticsearchServerError != null)
                        {
                            errorMessage = response.ElasticsearchServerError.Error?.Reason ?? "Ошибка сервера ElasticSearch";
                        }
                        else if (!string.IsNullOrEmpty(response.DebugInformation))
                        {
                            errorMessage = response.DebugInformation;
                        }
                        
                        failedArticles.Add((doc.Title, doc.Url, errorMessage));
                        Console.WriteLine($"❌ Ошибка при индексации: \"{doc.Title}\"");
                        Console.WriteLine($"   URL: {doc.Url}");
                        Console.WriteLine($"   Причина: {errorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    allSuccess = false;
                    failedArticles.Add((doc.Title, doc.Url, ex.Message));
                    Console.WriteLine($"❌ Исключение при индексации: \"{doc.Title}\"");
                    Console.WriteLine($"   URL: {doc.Url}");
                    Console.WriteLine($"   Ошибка: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine($"   Внутренняя ошибка: {ex.InnerException.Message}");
                    }
                }
            }
        }

        // Вычисляем количество удаленных дубликатов
        int duplicatesRemoved = duplicateIds.Any() 
            ? duplicateIds.Sum(g => g.Count() - 1) 
            : 0;

        // Выводим итоговую статистику
        Console.WriteLine();
        Console.WriteLine($"Статистика индексации:");
        Console.WriteLine($"    Успешно проиндексировано: {successCount} из {documents.Count}");
        Console.WriteLine($"    Не удалось: {failedArticles.Count} из {documents.Count}");
        
        if (duplicatesRemoved > 0)
        {
            Console.WriteLine($"    Примечание: {duplicatesRemoved} документов были удалены как дубликаты ID перед индексацией");
        }
        
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

        return (allSuccess, successCount, duplicatesRemoved);
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
                    Must = new[] { queryContainer },
                    Filter = filters
                };
            }
        }

        // Упрощенный запрос без подсветки для совместимости
        var searchRequest = new SearchRequest(IndexName)
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
            var existsResponse = await _client.Indices.ExistsAsync(IndexName);
            if (!existsResponse.Exists)
            {
                // Индекс не существует, возвращаем 0
                return 0;
            }
            
            var response = await _client.CountAsync<ArticleDocument>(c => c
                .Indices(IndexName)
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
            var existsResponse = await _client.Indices.ExistsAsync(IndexName);
            
            if (existsResponse.Exists)
            {
                var deleteResponse = await _client.Indices.DeleteAsync(IndexName);
                
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
