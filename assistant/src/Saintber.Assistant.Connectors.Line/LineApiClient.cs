using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Saintber.Assistant.Connectors.Line;

/// <summary>LINE 的 reply、push 與 loading HTTP 傳輸；逾時與重送政策由 Core 管理。</summary>
public sealed class LineApiClient
{
    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly string _accessToken;
    private readonly ILogger _logger;

    public LineApiClient(HttpClient client, Uri baseUri, string accessToken, ILogger logger)
    {
        _client = client;
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _baseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/");
        _accessToken = accessToken;
        _logger = logger;
    }

    public Task ReplyAsync(string replyToken, string text, CancellationToken cancellationToken) =>
        PostAsync("v2/bot/message/reply", new { replyToken, messages = new[] { new { type = "text", text } } }, cancellationToken);

    public Task PushAsync(string destination, string text, CancellationToken cancellationToken) =>
        PostAsync("v2/bot/message/push", new { to = destination, messages = new[] { new { type = "text", text } } }, cancellationToken);

    public Task LoadingAsync(string userId, int seconds, CancellationToken cancellationToken) =>
        PostAsync("v2/bot/chat/loading/start", new { chatId = userId, loadingSeconds = seconds }, cancellationToken);

    private async Task PostAsync<T>(string path, T body, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            request.Content = JsonContent.Create(body);
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("LINE API request cancelled.", cancellationToken);
        }
        catch (Exception)
        {
            _logger.LogWarning("LINE API request failed");
            throw new HttpRequestException("LINE API request failed.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                _logger.LogWarning("LINE API returned HTTP {StatusCode}", status);
                throw new HttpRequestException($"LINE API returned HTTP {status}.", null, response.StatusCode);
            }
        }
    }
}
