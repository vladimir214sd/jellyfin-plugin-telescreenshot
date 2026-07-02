using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TeleScreenshot.Services;

/// <summary>
/// Typed HttpClient wrapper for the Telegram Bot API. Resolved as a singleton through DI
/// (the inner <see cref="HttpClient"/> is managed by <c>IHttpClientFactory</c>).
/// </summary>
public sealed class TelegramClient
{
    private const string BaseUrl = "https://api.telegram.org/bot";

    public HttpClient Http { get; }

    public TelegramClient(HttpClient http)
    {
        Http = http;
    }

    /// <summary>Builds the absolute URL for a bot API method.</summary>
    public string Url(string botToken, string method) => $"{BaseUrl}{botToken}/{method}";
}

/// <summary>
/// Result of a Telegram API call.
/// </summary>
public sealed class TelegramResult
{
    public bool Ok { get; init; }

    /// <summary>Human-readable message for the UI on success or failure.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Bot username (only populated by <see cref="TelegramService.TestAsync"/>)</summary>
    public string? Username { get; init; }
}

/// <summary>
/// High-level service for verifying the bot token and sending screenshots.
/// </summary>
public sealed class TelegramService
{
    private readonly TelegramClient _client;
    private readonly ILogger<TelegramService> _logger;

    public TelegramService(TelegramClient client, ILogger<TelegramService> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Calls <c>getMe</c> to verify that the bot token is valid. Used by the "Test connection"
    /// button in the settings page.
    /// </summary>
    public async Task<TelegramResult> TestAsync(string botToken, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _client.Http.GetAsync(_client.Url(botToken, "getMe"), ct)
                .ConfigureAwait(false);
            return await ParseAsync(resp, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram getMe request failed");
            return new TelegramResult { Ok = false, Message = "Network error: " + ex.Message };
        }
    }

    /// <summary>
    /// Sends a PNG screenshot to the chat via <c>sendPhoto</c> using multipart/form-data.
    /// Telegram does not accept base64/data-URI directly, so the bytes are uploaded as a
    /// binary file part under the field name <c>photo</c>.
    /// </summary>
    public async Task<TelegramResult> SendPhotoAsync(
        string botToken,
        string chatId,
        byte[] imageBytes,
        string fileName,
        string mimeType,
        string? caption,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();

        form.Add(new StringContent(chatId), "chat_id");

        var fileContent = new ByteArrayContent(imageBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(fileContent, "photo", fileName);

        if (!string.IsNullOrEmpty(caption))
        {
            // Telegram caps captions at 1024 chars; truncate to avoid a 400.
            string cap = caption.Length > 1024 ? caption[..1024] : caption;
            form.Add(new StringContent(cap), "caption");
        }

        try
        {
            using var resp = await _client.Http.PostAsync(_client.Url(botToken, "sendPhoto"), form, ct)
                .ConfigureAwait(false);
            var result = await ParseAsync(resp, ct).ConfigureAwait(false);
            return result.Ok
                ? result
                : new TelegramResult { Ok = false, Message = "Telegram error: " + result.Message };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram sendPhoto request failed");
            return new TelegramResult { Ok = false, Message = "Network error: " + ex.Message };
        }
    }

    /// <summary>
    /// Parses a generic Telegram JSON response (<c>{ ok, result?, error_code?, description? }</c>)
    /// into a <see cref="TelegramResult"/>. Returns <c>ok=true</c> when the body's <c>ok</c> is true.
    /// </summary>
    private async Task<TelegramResult> ParseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        bool ok = false;
        string description = string.Empty;
        int errorCode = 0;
        string? username = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ok", out var okEl))
            {
                ok = okEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("description", out var descEl))
            {
                description = descEl.GetString() ?? string.Empty;
            }

            if (doc.RootElement.TryGetProperty("error_code", out var codeEl))
            {
                errorCode = codeEl.GetInt32();
            }

            // getMe result: { ok, result: { id, is_bot, first_name, username, ... } }
            if (doc.RootElement.TryGetProperty("result", out var resEl)
                && resEl.ValueKind == JsonValueKind.Object
                && resEl.TryGetProperty("username", out var unameEl))
            {
                username = unameEl.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON body — fall back to raw text + status code.
            return new TelegramResult
            {
                Ok = false,
                Message = $"HTTP {(int)resp.StatusCode}: {Truncate(body)}"
            };
        }

        return new TelegramResult
        {
            Ok = ok,
            Username = username,
            Message = ok
                ? (username is null ? "OK" : username)
                : (errorCode > 0 ? $"{errorCode}: {description}" : $"HTTP {(int)resp.StatusCode}: {description}")
        };
    }

    private static string Truncate(string s, int max = 200) =>
        s.Length <= max ? s : s[..max] + "…";
}
