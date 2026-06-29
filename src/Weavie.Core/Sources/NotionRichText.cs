using System.Text;
using System.Text.Json;

namespace Weavie.Core.Sources;

/// <summary>
/// Renders a Notion <c>rich_text</c> array to escaped inline HTML — bold/italic/strikethrough/underline/inline-code,
/// links (gated by <see cref="HtmlEscape.SafeUrl"/>), and text/background color (a fixed class allowlist, never an
/// inline style). Pure and static, unit-tested without the network.
/// </summary>
internal static class NotionRichText {
	private static readonly HashSet<string> AllowedColors = new(StringComparer.Ordinal) {
		"gray", "brown", "orange", "yellow", "green", "blue", "purple", "pink", "red",
		"gray_background", "brown_background", "orange_background", "yellow_background",
		"green_background", "blue_background", "purple_background", "pink_background", "red_background",
	};

	/// <summary>Renders the <c>rich_text</c> property of <paramref name="content"/> to inline HTML (empty when absent).</summary>
	public static string Render(JsonElement content) =>
		content.TryGetProperty("rich_text", out var runs) && runs.ValueKind == JsonValueKind.Array ? RenderRuns(runs) : string.Empty;

	/// <summary>Renders a <c>rich_text</c> array to inline HTML.</summary>
	public static string RenderRuns(JsonElement runs) {
		var builder = new StringBuilder();
		foreach (var run in runs.EnumerateArray()) {
			builder.Append(RenderRun(run));
		}

		return builder.ToString();
	}

	private static string RenderRun(JsonElement run) {
		string html = HtmlEscape.Text(SourceJson.String(run, "plain_text"));
		if (html.Length == 0) {
			return string.Empty;
		}

		var annotations = run.TryGetProperty("annotations", out var a) ? a : default;
		html = Wrap(Flag(annotations, "code"), "code", html);
		html = Wrap(Flag(annotations, "bold"), "strong", html);
		html = Wrap(Flag(annotations, "italic"), "em", html);
		html = Wrap(Flag(annotations, "underline"), "u", html);
		html = Wrap(Flag(annotations, "strikethrough"), "s", html);

		if (ColorClass(SourceJson.String(annotations, "color")) is { } cssClass) {
			html = $"<span class=\"{cssClass}\">{html}</span>";
		}

		if (LinkUrl(run) is { } href && HtmlEscape.SafeUrl(href) is { } safe) {
			html = $"<a href=\"{safe}\">{html}</a>";
		}

		return html;
	}

	private static string Wrap(bool on, string tag, string inner) => on ? $"<{tag}>{inner}</{tag}>" : inner;

	private static bool Flag(JsonElement annotations, string name) =>
		annotations.ValueKind == JsonValueKind.Object && annotations.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

	// The run's link: the resolved top-level href, else the inline text.link.url.
	private static string? LinkUrl(JsonElement run) {
		if (SourceJson.String(run, "href") is { Length: > 0 } href) {
			return href;
		}

		return run.TryGetProperty("text", out var text) && text.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.Object
			&& SourceJson.String(link, "url") is { Length: > 0 } url ? url : null;
	}

	// A Notion color → a fixed class; default/unknown → null (the stylesheet owns the actual colors).
	internal static string? ColorClass(string color) {
		if (color.Length == 0 || color == "default" || !AllowedColors.Contains(color)) {
			return null;
		}

		return color.EndsWith("_background", StringComparison.Ordinal)
			? "wv-bg-" + color[..^"_background".Length]
			: "wv-color-" + color;
	}
}
