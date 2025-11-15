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
        // HttpFetcher инкапсулирует повторные попытки и единые настройки HTTP клиента
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
        // Отбрасываем некорректные аргументы сразу, чтобы не делать лишние запросы
        if (string.IsNullOrWhiteSpace(channel) || postId <= 0) return null;

        // Telegram предоставляет количество комментариев на embed-странице обсуждения
        var discussionUrl = $"https://t.me/{channel}/{postId}?embed=1&discussion=1";

        try
        {
            // Загружаем страницу с использованием повторных попыток, так как Telegram может временно отдавать ошибки
            var html = await _httpFetcher.FetchWithRetryAsync(discussionUrl, maxRetries);
            if (string.IsNullOrEmpty(html)) return null;

            // В первую очередь ищем javascript-инициализацию, где встречается точное значение счётчика
            var initMatch = Regex.Match(html, @"TWidgetDiscussion\.init\(\{\s*""comments_cnt""\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (initMatch.Success && int.TryParse(initMatch.Groups[1].Value, out var commentsFromInit))
                return commentsFromInit;

            // Если скрипта нет, пробуем извлечь число из видимого блока шапки обсуждения
            var headerMatch = Regex.Match(html, @"<span class=""js-header"">(\d+)\s+comments?</span>", RegexOptions.IgnoreCase);
            if (headerMatch.Success && int.TryParse(headerMatch.Groups[1].Value, out var commentsFromHeader))
                return commentsFromHeader;
        }
        catch (Exception)
        {
            // Телеграм часто ограничивает запросы, поэтому проглатываем ошибки и возвращаем null
        }

        return null;
    }
}

