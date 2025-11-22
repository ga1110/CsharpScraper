using System.Text.Json.Serialization;

namespace Searcher.Models;

/// <summary>
/// Модель документа статьи для индексации в ElasticSearch
/// </summary>
public class ArticleDocument
{
    /// <summary>
    /// Уникальный идентификатор документа
    /// </summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>
    /// Заголовок статьи
    /// </summary>
    public string Title { get; set; } = string.Empty;
    
    /// <summary>
    /// URL статьи
    /// </summary>
    public string Url { get; set; } = string.Empty;
    
    /// <summary>
    /// Содержимое статьи 
    /// </summary>
    public string Content { get; set; } = string.Empty;
    
    /// <summary>
    /// Категория статьи
    /// </summary>
    public string Category { get; set; } = string.Empty;
    
    /// <summary>
    /// Дата публикации статьи
    /// </summary>
    public DateTime? PublishDate { get; set; }
    
    /// <summary>
    /// Автор статьи
    /// </summary>
    public string Author { get; set; } = string.Empty;
    
    /// <summary>
    /// Количество комментариев
    /// </summary>
    public int? CommentCount { get; set; }
    
    /// <summary>
    /// URL изображения статьи
    /// </summary>
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Исходный score от ElasticSearch (_score)
    /// </summary>
    [JsonIgnore]
    public double ElasticScore { get; set; }

    /// <summary>
    /// Позиция документа до переупорядочивания
    /// </summary>
    [JsonIgnore]
    public int SearchRank { get; set; }

    /// <summary>
    /// Предсказанный скор reranker-моделью
    /// </summary>
    [JsonIgnore]
    public double RerankerScore { get; set; }
}
