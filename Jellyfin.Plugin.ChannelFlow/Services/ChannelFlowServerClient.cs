using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.ChannelFlow.Services;

public sealed class ChannelFlowServerClient
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);

    private readonly IHttpClientFactory _http;

    public ChannelFlowServerClient(IHttpClientFactory http)
    {
        _http = http;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        string? serverUrl,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var baseUrl = (serverUrl ?? config?.ServerUrl ?? string.Empty).Trim().TrimEnd('/');
        var key = string.IsNullOrWhiteSpace(apiKey) ? config?.ApiKey : apiKey;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new ConnectionTestResult(false, "Enter a ChannelFlow Server URL.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new ConnectionTestResult(false, "Server URL must be an absolute http or https address.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TestTimeout);

        try
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                "/api/plugin/live-tv-urls",
                null,
                cts.Token,
                baseUrl,
                key);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new ConnectionTestResult(false, "Reached ChannelFlow Server, but the API key was rejected.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionTestResult(
                    false,
                    $"ChannelFlow Server responded with {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return new ConnectionTestResult(true, "Connected to ChannelFlow Server.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResult(false, "Timed out waiting for ChannelFlow Server.");
        }
        catch (HttpRequestException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            if (ex.InnerException is SocketException)
            {
                return new ConnectionTestResult(
                    false,
                    $"Could not reach ChannelFlow Server ({detail}). Use a hostname or LAN IP this Jellyfin container can resolve, on the same Docker network.");
            }

            return new ConnectionTestResult(false, detail);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, ex.Message);
        }
    }

    public async Task PostJsonAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, cancellationToken);
        await EnsureSuccessAsync(response, path, cancellationToken);
    }

    public async Task PatchJsonAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Patch, path, body, cancellationToken);
        await EnsureSuccessAsync(response, path, cancellationToken);
    }

    public async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        await EnsureSuccessAsync(response, path, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 500)
        {
            body = body[..500];
        }

        throw new HttpRequestException(
            $"ChannelFlow Server {(int)response.StatusCode} {response.ReasonPhrase} for {path}: {body}");
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        return SendAsync(
            method,
            path,
            body,
            cancellationToken,
            (config?.ServerUrl ?? "http://127.0.0.1:8097").TrimEnd('/'),
            config?.ApiKey);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        string baseUrl,
        string? apiKey)
    {
        var client = _http.CreateClient("channelflow");
        using var request = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

public sealed record ConnectionTestResult(bool Ok, string Message);
