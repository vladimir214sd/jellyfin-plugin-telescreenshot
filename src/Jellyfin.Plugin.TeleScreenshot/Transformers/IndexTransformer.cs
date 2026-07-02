using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TeleScreenshot.Transformers;

/// <summary>
/// In-memory transformation of <c>index.html</c> served by jellyfin-web.
///
/// Contract required by the File Transformation plugin (verified against the upstream source):
/// the registered method must be <c>public static</c>, take exactly one parameter, and return
/// <see cref="string"/> (the upstream casts the result via <c>(string)</c>, so it must NOT be
/// <c>Task&lt;string&gt;</c>). The parameter type is deserialized from a Newtonsoft JObject of
/// the shape <c>{ "contents": "&lt;full file text&gt;" }</c> via <c>JObject.ToObject(Type)</c>,
/// so any POCO with a matching <c>Contents</c> property works. We use <see cref="Input"/>.
/// </summary>
public static class IndexTransformer
{
    /// <summary>Marker comment left at the injection point; makes the transform idempotent.</summary>
    private const string Marker = "<!--telescreenshot-injected-->";

    /// <summary>The exact fragment inserted before <c>&lt;/body&gt;</c>.</summary>
    private const string InjectedFragment = Marker +
        "<script src=\"configurationpage?name=telescreenshot.js\"></script>" +
        "<link rel=\"stylesheet\" href=\"configurationpage?name=telescreenshot.css\" />";

    /// <summary>
    /// Input deserialized from the File Transformation callback payload.
    /// </summary>
    public sealed class Input
    {
        /// <summary>The full text of the file being transformed.</summary>
        [JsonPropertyName("contents")]
        public string Contents { get; set; } = string.Empty;
    }

    /// <summary>
    /// Injects the screenshot script/link into <paramref name="input"/>'s contents.
    /// Idempotent: if the marker is already present the contents are returned unchanged, which
    /// keeps the file stable across repeated requests.
    /// </summary>
    public static string Transform(Input input)
    {
        if (input is null || string.IsNullOrEmpty(input.Contents))
        {
            return input?.Contents ?? string.Empty;
        }

        string contents = input.Contents;
        if (contents.Contains(Marker, StringComparison.Ordinal))
        {
            return contents;
        }

        int bodyClose = contents.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose < 0)
        {
            // No </body> — unusual, but append at the end as a safe fallback.
            return contents + InjectedFragment;
        }

        return contents.Insert(bodyClose, InjectedFragment);
    }
}
