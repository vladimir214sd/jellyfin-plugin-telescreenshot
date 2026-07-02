using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TeleScreenshot.Models;

/// <summary>
/// Body of <c>POST /TeleScreenshot/Send</c>. Posted by the injected <c>telescreenshot.js</c>.
/// </summary>
public sealed class ScreenshotRequest
{
    /// <summary>
    /// Optional Jellyfin item id of the media being played (used to build the caption title).
    /// May be empty if the frontend could not determine it.
    /// </summary>
    [JsonPropertyName("itemId")]
    public string? ItemId { get; set; }

    /// <summary>
    /// Current playback position in 100-nanosecond ticks (Jellyfin's native unit).
    /// <c>null</c>/0 means "unknown".
    /// </summary>
    [JsonPropertyName("positionTicks")]
    public long? PositionTicks { get; set; }

    /// <summary>
    /// PNG screenshot as a base64 string. May be a full data URL
    /// (<c>data:image/png;base64,...</c>) or raw base64 — both are handled.
    /// </summary>
    [JsonPropertyName("imageBase64")]
    public string ImageBase64 { get; set; } = string.Empty;
}
