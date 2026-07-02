using System.Net.Mime;
using Jellyfin.Plugin.TeleScreenshot.Models;
using Jellyfin.Plugin.TeleScreenshot.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TeleScreenshot.Api;

/// <summary>
/// HTTP endpoints consumed by the injected frontend. <c>/Send</c> is open to any authenticated
/// user (taking a screenshot is a client-side action); <c>/Test</c> requires elevation because
/// it reveals bot connectivity state and is meant for administrators.
/// </summary>
[ApiController]
[Route("[controller]")]
public class TelegramScreenshotController : ControllerBase
{
    private readonly ILogger<TelegramScreenshotController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly TelegramService _telegram;

    public TelegramScreenshotController(
        ILogger<TelegramScreenshotController> logger,
        ILibraryManager libraryManager,
        TelegramService telegram)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _telegram = telegram;
    }

    /// <summary>
    /// Sends a captured frame to Telegram.
    /// </summary>
    [HttpPost("Send")]
    [Authorize]
    public async Task<ActionResult<SendResponse>> Send([FromBody] ScreenshotRequest request)
    {
        var config = Plugin.Instance.Configuration;
        if (!config.Enabled)
        {
            return BadRequest(new SendResponse { Ok = false, Message = "Plugin is disabled." });
        }

        if (string.IsNullOrWhiteSpace(config.BotToken) || string.IsNullOrWhiteSpace(config.ChatId))
        {
            return BadRequest(new SendResponse
            {
                Ok = false,
                Message = "Bot token or chat id is not configured."
            });
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            return BadRequest(new SendResponse { Ok = false, Message = "No image data received." });
        }

        byte[] imageBytes;
        try
        {
            imageBytes = DecodeBase64Image(request.ImageBase64);
        }
        catch (FormatException ex)
        {
            return BadRequest(new SendResponse { Ok = false, Message = "Invalid image data: " + ex.Message });
        }

        string caption = BuildCaption(config.CaptionFormat, request.ItemId, request.PositionTicks);

        var result = await _telegram.SendPhotoAsync(
            config.BotToken,
            config.ChatId,
            imageBytes,
            fileName: "screenshot.png",
            mimeType: MediaTypeNames.Image.Png,
            caption: caption).ConfigureAwait(false);

        if (!result.Ok)
        {
            _logger.LogWarning("Telegram sendPhoto failed: {Message}", result.Message);
        }

        return Ok(new SendResponse
        {
            Ok = result.Ok,
            Message = result.Ok ? "Screenshot sent to Telegram." : ("Send failed: " + result.Message)
        });
    }

    /// <summary>
    /// Returns the subset of config the frontend needs to decide whether to show the button.
    /// No secrets (bot token / chat id) are exposed. Open to any authenticated user so the
    /// injected script can read it on every page load.
    /// </summary>
    [HttpGet("Config")]
    [Authorize]
    public ActionResult<PublicConfig> Config()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(new PublicConfig
        {
            Enabled = config.Enabled,
            ShowCaptureToast = config.ShowCaptureToast
        });
    }

    /// <summary>
    /// Verifies the configured bot token via <c>getMe</c>. Admin only.
    /// </summary>
    [HttpPost("Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<TestResponse>> Test()
    {
        var config = Plugin.Instance.Configuration;
        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            return BadRequest(new TestResponse { Ok = false, Message = "Bot token is not configured." });
        }

        var result = await _telegram.TestAsync(config.BotToken).ConfigureAwait(false);
        return Ok(new TestResponse
        {
            Ok = result.Ok,
            Message = result.Message,
            Username = result.Username
        });
    }

    /// <summary>
    /// Decodes either a raw base64 string or a <c>data:image/png;base64,...</c> data URL to bytes.
    /// </summary>
    private static byte[] DecodeBase64Image(string raw)
    {
        string base64 = raw.Trim();
        int comma = base64.IndexOf(',');
        if (comma >= 0 && base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            base64 = base64[(comma + 1)..];
        }
        return Convert.FromBase64String(base64);
    }

    /// <summary>
    /// Formats the photo caption by substituting <c>{title}</c> and <c>{timecode}</c> in the
    /// configured template.
    /// </summary>
    private string BuildCaption(string template, string? itemId, long? positionTicks)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        string title = ResolveTitle(itemId);
        string timecode = positionTicks.HasValue && positionTicks.Value > 0
            ? TimeSpan.FromTicks(positionTicks.Value).ToString(@"hh\:mm\:ss")
            : "--:--:--";

        return template
            .Replace("{title}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{timecode}", timecode, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a human-readable title for the playing item. For episodes this prefers
    /// <c>"SeriesName — EpisodeName"</c>; otherwise the item <c>Name</c> is used.
    /// </summary>
    private string ResolveTitle(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId, out var guid))
        {
            return "Unknown title";
        }

        try
        {
            var item = _libraryManager.GetItemById(guid);
            if (item is null)
            {
                return "Unknown title";
            }

            // SeriesName is declared on Episode (and a few other Series-bearing types), not on
            // BaseItem itself, so cast before reading.
            if (item is MediaBrowser.Controller.Entities.TV.Episode ep
                && !string.IsNullOrWhiteSpace(ep.SeriesName))
            {
                return $"{ep.SeriesName} — {ep.Name}";
            }

            return item.Name ?? "Unknown title";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve title for item {ItemId}", itemId);
            return "Unknown title";
        }
    }
}

/// <summary>Response body for <c>POST /TeleScreenshot/Send</c>.</summary>
public sealed class SendResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Public (secret-free) config read by the injected frontend. Returned by
/// <c>GET /TeleScreenshot/Config</c>.
/// </summary>
public sealed class PublicConfig
{
    [System.Text.Json.Serialization.JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("showCaptureToast")]
    public bool ShowCaptureToast { get; set; }
}

/// <summary>Response body for <c>POST /TeleScreenshot/Test</c>.</summary>
public sealed class TestResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public string? Username { get; set; }
}
