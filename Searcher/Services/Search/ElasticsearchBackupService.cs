using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core;
using Elastic.Clients.Elasticsearch.IndexManagement;
using System.Text.Json;
using Searcher.Models;
using Scraper.Models;

namespace Searcher.Services.Search;

/// <summary>
/// Сервис для создания и восстановления бэкапов Elasticsearch индексов
/// </summary>
public class ElasticsearchBackupService
{
    private readonly ElasticsearchClient _client;
    private readonly string _backupDirectory;

    public ElasticsearchBackupService(ElasticsearchClient client, string? backupDirectory = null)
    {
        _client = client;
        // По умолчанию используем папку backup в корне проекта
        if (backupDirectory == null)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            _backupDirectory = Path.Combine(projectRoot, "backup");
        }
        else
        {
            _backupDirectory = backupDirectory;
        }
    }

    /// <summary>
    /// Создает бэкап всех пользовательских индексов
    /// </summary>
    /// <param name="customPath">Пользовательский путь для сохранения бэкапа (опционально, не используется в новой логике)</param>
    /// <returns>Путь к созданному бэкапу</returns>
    public async Task<string> CreateBackupAsync(string? customPath = null)
    {
        // Используем фиксированную папку backup в корне проекта
        var backupPath = _backupDirectory;
        
        // Удаляем старый бэкап, если он существует
        if (Directory.Exists(backupPath))
        {
            Directory.Delete(backupPath, true);
        }
        
        Directory.CreateDirectory(backupPath);

        try
        {
            // Используем простой подход - работаем только с индексом articles
            var articlesIndex = "articles";
            var existsResponse = await _client.Indices.ExistsAsync(articlesIndex);
            
            if (!existsResponse.Exists)
            {
                throw new Exception("Индекс 'articles' не найден");
            }

            var userIndices = new List<string> { articlesIndex };

            var backupMetadata = new BackupMetadata
            {
                Timestamp = DateTime.UtcNow,
                IndicesCount = userIndices.Count,
                Indices = new List<IndexBackupInfo>()
            };

            foreach (var indexName in userIndices)
            {
                // Получаем количество документов
                var countResponse = await _client.CountAsync<ArticleDocument>(c => c.Indices(indexName));
                var documentCount = countResponse.IsValidResponse ? (int)countResponse.Count : 0;
                
                var indexBackupInfo = new IndexBackupInfo
                {
                    Name = indexName,
                    DocumentCount = documentCount
                };

                // Экспортируем маппинги (упрощенно - сохраняем базовую структуру)
                var mappingInfo = new
                {
                    mappings = new
                    {
                        properties = new
                        {
                            id = new { type = "keyword" },
                            title = new { type = "text", analyzer = "ru_text" },
                            content = new { type = "text", analyzer = "ru_text" },
                            url = new { type = "keyword" },
                            category = new { type = "keyword" },
                            author = new { type = "keyword" },
                            publishDate = new { type = "date" },
                            commentCount = new { type = "integer" }
                        }
                    }
                };
                var mappingPath = Path.Combine(backupPath, $"{indexName}_mapping.json");
                await File.WriteAllTextAsync(mappingPath, JsonSerializer.Serialize(mappingInfo, new JsonSerializerOptions { WriteIndented = true }));
                indexBackupInfo.MappingFile = $"{indexName}_mapping.json";

                // Экспортируем настройки (упрощенно - сохраняем базовую информацию)
                var settingsInfo = new
                {
                    index = new
                    {
                        number_of_shards = 1,
                        number_of_replicas = 0
                    }
                };
                var settingsPath = Path.Combine(backupPath, $"{indexName}_settings.json");
                await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settingsInfo, new JsonSerializerOptions { WriteIndented = true }));
                indexBackupInfo.SettingsFile = $"{indexName}_settings.json";

                // Экспортируем данные
                var documents = await ExportIndexDataAsync(indexName);
                var dataPath = Path.Combine(backupPath, $"{indexName}_data.json");
                await File.WriteAllTextAsync(dataPath, JsonSerializer.Serialize(documents, new JsonSerializerOptions { WriteIndented = true }));
                indexBackupInfo.DataFile = $"{indexName}_data.json";
                indexBackupInfo.ActualDocumentCount = documents.Count;

                backupMetadata.Indices.Add(indexBackupInfo);
            }

            // Сохраняем метаданные
            var metadataPath = Path.Combine(backupPath, "backup_metadata.json");
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(backupMetadata, new JsonSerializerOptions { WriteIndented = true }));

            return backupPath;
        }
        catch (Exception ex)
        {
            throw new Exception($"Ошибка при создании бэкапа: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Восстанавливает индексы из бэкапа
    /// </summary>
    /// <param name="backupPath">Путь к директории с бэкапом (если null, используется стандартная папка backup)</param>
    /// <param name="force">Перезаписывать существующие индексы</param>
    public async Task RestoreBackupAsync(string? backupPath = null, bool force = false)
    {
        // Если путь не указан, используем стандартную папку backup
        backupPath ??= _backupDirectory;
        
        if (!Directory.Exists(backupPath))
        {
            throw new DirectoryNotFoundException($"Директория бэкапа не найдена: {backupPath}");
        }

        var dataFiles = Directory.GetFiles(backupPath, "*_data.json");
        
        if (dataFiles.Length == 0)
        {
            throw new FileNotFoundException("Файлы данных не найдены в директории бэкапа");
        }

        foreach (var dataFile in dataFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(dataFile);
            var indexName = fileName.Replace("_data", "");

            var existsResponse = await _client.Indices.ExistsAsync(indexName);
            
            if (existsResponse.Exists)
            {
                if (!force)
                {
                    throw new InvalidOperationException($"Индекс '{indexName}' уже существует. Используйте --force для перезаписи.");
                }
                
                await _client.Indices.DeleteAsync(indexName);
            }

            await RestoreIndexAsync(backupPath, indexName);
        }

        await _client.Indices.RefreshAsync();
    }

    private async Task<List<ArticleDocument>> ExportIndexDataAsync(string indexName)
    {
        var documents = new List<ArticleDocument>();
        
        // Используем простой поиск для получения всех документов
        var searchResponse = await _client.SearchAsync<ArticleDocument>(s => s
            .Index(indexName)
            .Size(10000)
            .Query(q => q.MatchAll()));

        if (!searchResponse.IsValidResponse)
        {
            throw new Exception($"Ошибка при получении данных из индекса {indexName}: {searchResponse.DebugInformation}");
        }

        documents.AddRange(searchResponse.Documents);
        return documents;
    }

    private async Task RestoreIndexAsync(string backupPath, string indexName)
    {
        // Подготавливаем создание индекса
        var createIndexRequest = new CreateIndexRequest(indexName);

        // Восстанавливаем настройки
        var settingsFile = Path.Combine(backupPath, $"{indexName}_settings.json");
        if (File.Exists(settingsFile))
        {
            var settingsJson = await File.ReadAllTextAsync(settingsFile);
            // Здесь можно добавить логику извлечения пользовательских настроек
        }

        // Восстанавливаем маппинги
        var mappingFile = Path.Combine(backupPath, $"{indexName}_mapping.json");
        if (File.Exists(mappingFile))
        {
            var mappingJson = await File.ReadAllTextAsync(mappingFile);
            // Здесь можно добавить логику применения маппингов
        }

        // Создаем индекс
        var createResponse = await _client.Indices.CreateAsync(createIndexRequest);
        if (!createResponse.IsValidResponse)
        {
            throw new Exception($"Не удалось создать индекс {indexName}: {createResponse.DebugInformation}");
        }

        // Восстанавливаем данные
        var dataFile = Path.Combine(backupPath, $"{indexName}_data.json");
        if (File.Exists(dataFile))
        {
            var dataJson = await File.ReadAllTextAsync(dataFile);
            var documents = JsonSerializer.Deserialize<List<ArticleDocument>>(dataJson);
            
            if (documents?.Any() == true)
            {
                // Используем bulk API для загрузки документов
                var bulkResponse = await _client.BulkAsync(b => b
                    .Index(indexName)
                    .IndexMany(documents));

                if (!bulkResponse.IsValidResponse || bulkResponse.Errors)
                {
                    var errorCount = bulkResponse.Items.Count(i => i.Error != null);
                    throw new Exception($"Не удалось загрузить {errorCount} документов");
                }
            }
        }
    }
}

/// <summary>
/// Метаданные бэкапа
/// </summary>
public class BackupMetadata
{
    public DateTime Timestamp { get; set; }
    public int IndicesCount { get; set; }
    public List<IndexBackupInfo> Indices { get; set; } = new();
}

/// <summary>
/// Информация о бэкапе индекса
/// </summary>
public class IndexBackupInfo
{
    public string Name { get; set; } = string.Empty;
    public int DocumentCount { get; set; }
    public int ActualDocumentCount { get; set; }
    public string? MappingFile { get; set; }
    public string? SettingsFile { get; set; }
    public string? DataFile { get; set; }
}
