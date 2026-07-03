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

    /// <summary>
    /// Seconds to wait before retrying, as reported by Telegram flood control
    /// (<c>parameters.retry_after</c> on a 429). Null when not a rate-limit response.
    /// </summary>
    public int? RetryAfter { get; init; }
}

/// <summary>
/// A single photo plus its on-wire metadata for an album send.
/// </summary>
public sealed record ActorPhoto
{
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public string FileName { get; init; } = "actor.jpg";
    public string MimeType { get; init; } = "image/jpeg";
    /// <summary>Per-photo caption (e.g. "Keanu Reeves — Neo"). Only the first in a group is shown.</summary>
    public string? Caption { get; init; }
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
    /// Sends an album of 2-10 photos via <c>sendMediaGroup</c> using multipart/form-data.
    /// Each binary part is named <c>photoN</c> and referenced in the <c>media</c> JSON array as
    /// <c>attach://photoN</c>. Only the first photo's caption is rendered by Telegram clients.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="photos"/> does not contain 2-10 items (the API hard limits).
    /// </exception>
    public async Task<TelegramResult> SendMediaGroupAsync(
        string botToken,
        string chatId,
        IReadOnlyList<ActorPhoto> photos,
        string? caption = null,
        CancellationToken ct = default)
    {
        if (photos.Count is < 2 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(photos),
                "sendMediaGroup requires 2-10 photos.");
        }

        using var form = new MultipartFormDataContent();

        form.Add(new StringContent(chatId), "chat_id");

        // Build the media JSON array with Utf8JsonWriter (precise control over which item
        // carries the caption). Only index 0 gets caption + parse_mode.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            for (int i = 0; i < photos.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "photo");
                writer.WriteString("media", $"attach://photo{i}");
                if (i == 0 && !string.IsNullOrEmpty(caption))
                {
                    string cap = caption.Length > 1024 ? caption[..1024] : caption;
                    writer.WriteString("caption", cap);
                    writer.WriteString("parse_mode", "HTML");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        string mediaJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        form.Add(new StringContent(mediaJson), "media");

        // Binary parts: the field name "photoN" MUST match the attach://photoN token above.
        for (int i = 0; i < photos.Count; i++)
        {
            var photo = photos[i];
            var part = new ByteArrayContent(photo.Bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(photo.MimeType);
            form.Add(part, $"photo{i}", photo.FileName);
        }

        try
        {
            using var resp = await _client.Http.PostAsync(_client.Url(botToken, "sendMediaGroup"), form, ct)
                .ConfigureAwait(false);
            var result = await ParseAsync(resp, ct).ConfigureAwait(false);
            return result.Ok
                ? result
                : new TelegramResult { Ok = false, Message = "Telegram error: " + result.Message, RetryAfter = result.RetryAfter };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram sendMediaGroup request failed");
            return new TelegramResult { Ok = false, Message = "Network error: " + ex.Message };
        }
    }

    /// <summary>
    /// Sends a plain text message via <c>sendMessage</c>. Used for the list of actors that have
    /// no photo in Jellyfin.
    /// </summary>
    public async Task<TelegramResult> SendMessageAsync(
        string botToken,
        string chatId,
        string text,
        CancellationToken ct = default)
    {
        // Telegram caps message text at 4096 chars; truncate defensively.
        string body = text.Length > 4096 ? text[..4096] : text;

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId), "chat_id");
        form.Add(new StringContent(body), "text");

        try
        {
            using var resp = await _client.Http.PostAsync(_client.Url(botToken, "sendMessage"), form, ct)
                .ConfigureAwait(false);
            var result = await ParseAsync(resp, ct).ConfigureAwait(false);
            return result.Ok
                ? result
                : new TelegramResult { Ok = false, Message = "Telegram error: " + result.Message, RetryAfter = result.RetryAfter };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram sendMessage request failed");
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
        int? retryAfter = null;

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

            // Flood control: { parameters: { retry_after: <seconds> } }
            if (doc.RootElement.TryGetProperty("parameters", out var parEl)
                && parEl.ValueKind == JsonValueKind.Object
                && parEl.TryGetProperty("retry_after", out var raEl)
                && raEl.TryGetInt32(out int ra))
            {
                retryAfter = ra;
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
            RetryAfter = retryAfter,
            Message = ok
                ? (username is null ? "OK" : username)
                : (errorCode > 0 ? $"{errorCode}: {description}" : $"HTTP {(int)resp.StatusCode}: {description}")
        };
    }

    private static string Truncate(string s, int max = 200) =>
        s.Length <= max ? s : s[..max] + "…";
}
