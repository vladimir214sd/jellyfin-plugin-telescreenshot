using System.Net;
using System.Net.Mime;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TeleScreenshot.Models;
using Jellyfin.Plugin.TeleScreenshot.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TeleScreenshot.Api;

/// <summary>
/// HTTP endpoints consumed by the injected frontend. <c>/Send</c> is open to any authenticated
/// user (taking a screenshot is a client-side action); <c>/Test</c> requires elevation because
/// it reveals bot connectivity state and is meant for administrators.
/// </summary>
/// <remarks>
/// The route is pinned to <c>TeleScreenshot</c> (not derived from the class name) so the frontend
/// can call it by that exact path regardless of the controller class name.
/// </remarks>
[ApiController]
[Route("TeleScreenshot")]
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

        // Always try to send the cast album after the screenshot, independently of whether the
        // screenshot succeeded — they are non-blocking concerns. Errors are merged into the toast.
        var actorsResult = await SendActorsAsync(config, request.ItemId).ConfigureAwait(false);

        // Aggregate: overall ok only if the screenshot went through. The actors part is reported
        // as additional info so the user sees both outcomes in the toast.
        bool overallOk = result.Ok;
        var parts = new List<string>(capacity: 2);
        parts.Add(result.Ok ? "Screenshot sent" : ("Screenshot failed: " + result.Message));
        if (!string.IsNullOrEmpty(actorsResult))
        {
            parts.Add(actorsResult);
        }

        return Ok(new SendResponse
        {
            Ok = overallOk,
            Message = string.Join(" | ", parts)
        });
    }

    /// <summary>
    /// Sends the cast of <paramref name="itemId"/> to Telegram: one or more photo albums
    /// (via sendMediaGroup, 10 photos each) for actors that have an image, followed by a single
    /// text message listing the actors without a photo. Always enabled — no per-plugin toggle.
    /// Returns a short human-readable summary string, or null if there was nothing to send
    /// (no itemId, no item, no actors).
    /// </summary>
    private async Task<string?> SendActorsAsync(
        Jellyfin.Plugin.TeleScreenshot.Configuration.PluginConfiguration config,
        string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId, out var guid))
        {
            return null;
        }

        MediaBrowser.Controller.Entities.BaseItem? item;
        try
        {
            item = _libraryManager.GetItemById(guid);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load item {ItemId} for cast lookup", itemId);
            return null;
        }

        if (item is null)
        {
            return null;
        }

        // BaseItem.People was removed in Jellyfin 10.11; people live behind ILibraryManager.
        IReadOnlyList<PersonInfo> people;
        try
        {
            people = _libraryManager.GetPeople(item);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load people for item {ItemId}", itemId);
            return null;
        }

        var actors = people
            .Where(p => p.Type == PersonKind.Actor)
            .OrderBy(p => p.SortOrder ?? int.MaxValue)
            .ThenBy(p => p.Name)
            .ToList();

        if (actors.Count == 0)
        {
            return null;
        }

        var withPhoto = new List<ActorPhoto>();
        var withoutPhoto = new List<string>();

        foreach (var actor in actors)
        {
            string label = string.IsNullOrWhiteSpace(actor.Role)
                ? actor.Name
                : $"{actor.Name} — {actor.Role}";

            if (TryGetActorPhoto(actor, out var photo))
            {
                withPhoto.Add(new ActorPhoto
                {
                    Bytes = photo.Bytes,
                    FileName = photo.FileName,
                    MimeType = photo.MimeType,
                    Caption = label
                });
            }
            else
            {
                withoutPhoto.Add(label);
            }
        }

        int albumsOk = 0;
        int albumsFail = 0;
        string? lastError = null;

        if (withPhoto.Count > 0)
        {
            string title = ResolveTitle(itemId);
            string albumCaption = string.IsNullOrEmpty(title)
                ? "🎬 Cast"
                : $"🎬 <b>{System.Net.WebUtility.HtmlEncode(title)}</b>";

            // Chunk into groups of at most 10 (sendMediaGroup hard limit). If the last chunk would
            // be a single photo, merge it into the previous chunk so every group has >= 2 items.
            var chunks = ChunkForAlbum(withPhoto, 10);

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                string? cap = i == 0 ? albumCaption : null;

                TelegramResult r;
                if (chunk.Count == 1)
                {
                    // Should not happen after ChunkForAlbum, but guard anyway: single photo via sendPhoto.
                    r = await _telegram.SendPhotoAsync(
                        config.BotToken, config.ChatId,
                        chunk[0].Bytes, chunk[0].FileName, chunk[0].MimeType, cap).ConfigureAwait(false);
                }
                else
                {
                    r = await _telegram.SendMediaGroupAsync(
                        config.BotToken, config.ChatId, chunk, cap).ConfigureAwait(false);
                }

                if (r.Ok) { albumsOk++; }
                else
                {
                    albumsFail++;
                    lastError = r.Message;
                    _logger.LogWarning("sendMediaGroup chunk {Index} failed: {Message}", i, r.Message);
                }

                // Flood control: respect retry_after on 429, otherwise a 1s gap between albums
                // to the same chat (~20 msg/min group ceiling).
                if (i < chunks.Count - 1)
                {
                    int delayMs = r.RetryAfter.HasValue ? (r.RetryAfter.Value + 1) * 1000 : 1000;
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
            }
        }

        // Final text message: actors without a photo (if any).
        bool textOk = true;
        if (withoutPhoto.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("🎭 В ролях (без фото):");
            foreach (var name in withoutPhoto)
            {
                sb.Append("\n• ").Append(name);
            }

            var tr = await _telegram.SendMessageAsync(config.BotToken, config.ChatId, sb.ToString())
                .ConfigureAwait(false);
            textOk = tr.Ok;
            if (!tr.Ok)
            {
                lastError = tr.Message;
                _logger.LogWarning("sendMessage (no-photo list) failed: {Message}", tr.Message);
            }
        }

        // Build a short summary for the toast.
        var summary = new List<string>();
        if (withPhoto.Count > 0)
        {
            summary.Add(albumsFail == 0
                ? $"{albumsOk} album(s) of cast sent"
                : $"{albumsOk} album(s) sent, {albumsFail} failed");
        }
        if (withoutPhoto.Count > 0)
        {
            summary.Add(textOk
                ? $"{withoutPhoto.Count} actor(s) without photo listed"
                : "text list failed");
        }
        if (lastError is not null && (albumsFail > 0 || !textOk))
        {
            summary.Add("last error: " + lastError);
        }

        return summary.Count > 0 ? string.Join("; ", summary) : null;
    }

    /// <summary>
    /// Resolves a Person's primary image to bytes. Returns false if the person has no image or
    /// the image file cannot be read (remote download failure, missing file, etc.). Never throws.
    /// </summary>
    private bool TryGetActorPhoto(PersonInfo person, out (byte[] Bytes, string FileName, string MimeType) photo)
    {
        photo = default;

        try
        {
            if (_libraryManager.GetItemById(person.Id) is not Person personItem)
            {
                return false;
            }

            ItemImageInfo? info = personItem.GetImageInfo(ImageType.Primary, 0);
            if (info is null)
            {
                return false;
            }

            // Remote images (never downloaded) must be materialised to a local file first.
            if (!info.IsLocalFile)
            {
                info = _libraryManager.ConvertImageToLocal(personItem, info, 0)
                    .GetAwaiter().GetResult();
            }

            if (string.IsNullOrWhiteSpace(info.Path) || !System.IO.File.Exists(info.Path))
            {
                return false;
            }

            byte[] bytes = System.IO.File.ReadAllBytes(info.Path);
            string ext = System.IO.Path.GetExtension(info.Path).ToLowerInvariant();
            string mimeType = ext switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };
            string safeName = Slugify(person.Name) + ext;
            photo = (bytes, safeName, mimeType);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read photo for person {Name}", person.Name);
            return false;
        }
    }

    /// <summary>
    /// Splits a list into chunks of at most <paramref name="size"/>, merging a trailing
    /// single-item chunk into the previous one so every returned chunk has 2..size items
    /// (sendMediaGroup rejects groups of size 1).
    /// </summary>
    private static List<List<ActorPhoto>> ChunkForAlbum(List<ActorPhoto> source, int size)
    {
        var chunks = new List<List<ActorPhoto>>();
        for (int i = 0; i < source.Count; i += size)
        {
            int take = Math.Min(size, source.Count - i);
            chunks.Add(source.GetRange(i, take));
        }

        // Merge a lone trailing item into the previous chunk.
        if (chunks.Count >= 2 && chunks[^1].Count == 1)
        {
            chunks[^2].AddRange(chunks[^1]);
            chunks.RemoveAt(chunks.Count - 1);
        }

        return chunks;
    }

    private static string Slugify(string name)
    {
        var sb = new StringBuilder();
        bool started = false;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                started = true;
            }
            else if (started)
            {
                sb.Append('_');
            }
        }
        return sb.Length == 0 ? "actor" : sb.ToString().TrimEnd('_');
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
