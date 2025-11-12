namespace Scrapper.Models;

public class Article
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime? PublishDate { get; set; }
    public string Author { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public int? ViewCount { get; set; }
    public int? CommentCount { get; set; }
    public string Excerpt { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty; // Политика, Общество, Наука, Экономика и т.д.
}


