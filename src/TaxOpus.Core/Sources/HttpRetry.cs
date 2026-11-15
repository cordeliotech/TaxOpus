using System.Net;

namespace TaxOpus.Core.Sources;

internal static class HttpRetry
{
    public static async Task<HttpResponseMessage> GetWithRetryAsync(
        HttpClient http,
        string url,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead,
        CancellationToken cancellationToken = default,
        int maxAttempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await http.GetAsync(url, completionOption, cancellationToken);
                if (IsTransientStatus(response.StatusCode) && attempt < maxAttempts)
                {
                    var delay = RetryDelay(response, attempt);
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientException(ex, cancellationToken))
            {
                response?.Dispose();
                await Task.Delay(Backoff(attempt), cancellationToken);
            }
            catch
            {
                response?.Dispose();
                throw;
            }
        }
    }

    public static async Task<string> GetStringWithRetryAsync(
        HttpClient http,
        string url,
        CancellationToken cancellationToken = default,
        int maxAttempts = 3)
    {
        using var response = await GetWithRetryAsync(http, url, HttpCompletionOption.ResponseContentRead, cancellationToken, maxAttempts);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests ||
        status == HttpStatusCode.RequestTimeout ||
        (int)status >= 500;

    private static bool IsTransientException(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException ||
        (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;
        }
        return Backoff(attempt);
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(16, Math.Pow(2, attempt - 1)));
}
