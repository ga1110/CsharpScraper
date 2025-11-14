using System.Text.RegularExpressions;

namespace Scraper.Services;

/// <summary>
/// Сервис для получения количества комментариев из Telegram-обсуждений статей
/// </summary>
public class CommentService
{
    private readonly HttpFetcher _httpFetcher;

    /// <summary>
    /// Инициализирует новый экземпляр CommentService с указанным HttpFetcher
    /// </summary>
    /// <param name="httpFetcher">HTTP клиент для выполнения запросов к Telegram</param>
    public CommentService(HttpFetcher httpFetcher)
    {
        _httpFetcher = httpFetcher;
    }

    /// <summary>
    /// Получает количество комментариев для указанного поста в Telegram канале из embed-страницы обсуждения
    /// </summary>
    /// <param name="channel">Название Telegram канала (без @)</param>
    /// <param name="postId">ID поста в канале</param>
    /// <param name="maxRetries">Максимальное количество повторных попыток при ошибках (по умолчанию 2)</param>
    /// <returns>Количество комментариев или null, если не удалось получить</returns>
    public async Task<int?> GetCommentCountAsync(string channel, int postId, int maxRetries = 2)
    {
        if (string.IsNullOrWhiteSpace(channel) || postId <= 0) return null;

        var discussionUrl = $"https://t.me/{channel}/{postId}?embed=1&discussion=1";

        try
        {
            var html = await _httpFetcher.FetchWithRetryAsync(discussionUrl, maxRetries);
            if (string.IsNullOrEmpty(html)) return null;

            var initMatch = Regex.Match(html, @"TWidgetDiscussion\.init\(\{\s*""comments_cnt""\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (initMatch.Success && int.TryParse(initMatch.Groups[1].Value, out var commentsFromInit))
                return commentsFromInit;

            var headerMatch = Regex.Match(html, @"<span class=""js-header"">(\d+)\s+comments?</span>", RegexOptions.IgnoreCase);
            if (headerMatch.Success && int.TryParse(headerMatch.Groups[1].Value, out var commentsFromHeader))
                return commentsFromHeader;
        }
        catch (Exception)
        {
        }

        return null;
    }
}

