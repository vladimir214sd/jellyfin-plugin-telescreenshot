using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TeleScreenshot.Configuration;

/// <summary>
/// Plugin settings, serialized to XML by Jellyfin's <see cref="MediaBrowser.Common.Plugins.BasePlugin{T}"/>.
/// Property names must match the JSON keys posted from <c>configPage.html</c>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Master switch. When false, the controller returns an error on every screenshot request
    /// and the injected button is hidden by the frontend.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Telegram bot token, in the form <c>123456789:ABC-DEF...</c>.
    /// Kept server-side only — never sent to the browser.
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Target chat id (numeric, e.g. <c>123456789</c>) or <c>@channelusername</c>.
    /// The recipient must have started a conversation with the bot first.
    /// </summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>
    /// Photo caption template. Placeholders: <c>{title}</c>, <c>{timecode}</c>.
    /// Example: <c>"{title} — {timecode}"</c>.
    /// </summary>
    public string CaptionFormat { get; set; } = "{title} — {timecode}";

    /// <summary>
    /// When true, a toast is shown on capture in addition to the "sending…" / result toast.
    /// </summary>
    public bool ShowCaptureToast { get; set; } = true;
}
