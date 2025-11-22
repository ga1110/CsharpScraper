using System.Net;

namespace Scraper.Services;

/// <summary>
/// Класс для выполнения HTTP-запросов с поддержкой повторных попыток при ошибках
/// </summary>
public class HttpFetcher
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Инициализирует новый экземпляр HttpFetcher с указанным HttpClient
    /// </summary>
    /// <param name="httpClient">HTTP клиент для выполнения запросов</param>
    public HttpFetcher(HttpClient httpClient)
    {
        // HttpClient создаётся снаружи, чтобы можно было разделять настройки (прокси, заголовки, куки)
        _httpClient = httpClient;
    }

    /// <summary>
    /// Выполняет HTTP GET запрос с повторными попытками при ошибках (5xx, таймауты), но не повторяет при 4xx ошибках (404 и т.д.)
    /// </summary>
    /// <param name="url">URL для загрузки</param>
    /// <param name="maxRetries">Максимальное количество повторных попыток (по умолчанию 3)</param>
    /// <returns>HTML содержимое страницы</returns>
    /// <exception cref="HttpRequestException">Выбрасывается при 4xx ошибках или если все попытки исчерпаны</exception>
    public async Task<string> FetchWithRetryAsync(string url, int maxRetries = 3)
    {
        // Пробуем выполнить запрос несколько раз, постепенно увеличивая задержку между попытками
        for (int i = 0; i < maxRetries; i++)
        {
            HttpResponseMessage? response = null;
            try
            {
                // Делаем обычный GET запрос; HttpClient сам позаботится о keep-alive и повторном использовании соединений
                response = await _httpClient.GetAsync(url);

                // Сохраняем числовой код, чтобы единообразно обрабатывать разные диапазоны статусов
                var statusCode = (int)response.StatusCode;

                if (statusCode >= 400 && statusCode < 500)
                    // На 4xx ошибки нет смысла делать повторные попытки — сразу пробрасываем исключение
                    throw new HttpRequestException(
                        $"Response status code does not indicate success: {statusCode} (Not Found)");

                // Если сервер вернул 2xx код — читаем тело и завершаем работу метода
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    response.Dispose();
                    response = null; // Помечаем как освобожденный, чтобы finally не пытался освободить снова
                    return content;
                }

                // Для остальных статусов (обычно 5xx) принудительно генерируем исключение, чтобы перейти в блок catch
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadAsStringAsync();
                response.Dispose();
                response = null; // Помечаем как освобожденный
                return result;
            }
            catch (HttpRequestException ex)
            {
                // Анализируем текст исключения: 404 и любые другие 4xx повторять бессмысленно
                // Сообщение обычно содержит "Response status code does not indicate success: 404 (Not Found)"
                if (ex.Message.Contains("404") || ex.Message.Contains("Not Found") ||
                    ex.Message.Contains("Response status code does not indicate success: 4"))
                    throw;

                // Все остальные ошибки (5xx, таймауты, обрыв соединения) можно повторить
                if (i == maxRetries - 1)
                    throw;

                // Линейно увеличиваем задержку: 1 сек, 2 сек, 3 сек
                await Task.Delay(1000 * (i + 1)); // Увеличиваем задержку с каждой попыткой
            }
            catch (Exception ex)
            {
                // На случай других исключений (например, TaskCanceledException) выполняем ту же проверку
                if (ex.Message.Contains("404") || ex.Message.Contains("Not Found") ||
                    ex.Message.Contains("Response status code does not indicate success: 4"))
                    throw;

                if (i == maxRetries - 1)
                    throw;

                // Делаем паузу и пробуем снова
                await Task.Delay(1000 * (i + 1)); // Увеличиваем задержку с каждой попыткой
            }
            finally
            {
                if (response != null)
                    response.Dispose();
            }
        }
        // Если добрались сюда, значит все попытки были исчерпаны — сообщаем об этом вызывающему коду
        throw new Exception($"Не удалось загрузить {url} после {maxRetries} попыток");
    }
}

