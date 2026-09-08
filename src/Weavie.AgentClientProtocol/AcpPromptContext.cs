namespace Weavie.AgentClientProtocol;

internal static class AcpPromptContext {
	internal const string InstructionsUri = "weavie://instructions";

	internal static bool IsInjectedUri(string? value) => value == InstructionsUri
		|| (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile && uri.Fragment == "#selection");

	// ACP adapters flatten embedded resources into a link followed by an opaque context envelope.
	internal static string RemoveInjectedText(string text) {
		while (text.AsSpan().TrimEnd().EndsWith("\n</context>", StringComparison.Ordinal)) {
			int start = FindEnvelopeStart(text);
			if (start < 0) break;
			text = text[..start];
			if (string.IsNullOrWhiteSpace(text)) return string.Empty;
		}
		return text;
	}

	private static int FindEnvelopeStart(string text) {
		const string opening = "<context ref=\"";
		int before = text.Length;
		while (before > 0) {
			int start = text.LastIndexOf(opening, before - 1, StringComparison.Ordinal);
			if (start < 0) break;
			before = start;
			int uriStart = start + opening.Length;
			int uriEnd = text.IndexOf("\">", uriStart, StringComparison.Ordinal);
			if (uriEnd < 0 || !Uri.TryCreate(text[uriStart..uriEnd], UriKind.Absolute, out var resource)) continue;
			string uri = text[uriStart..uriEnd];
			string link = resource.IsFile ? $"[@{uri[(uri.LastIndexOf('/') + 1)..]}]({uri})" : uri;
			int linkEnd = start;
			if (linkEnd == 0 || text[--linkEnd] != '\n') continue;
			if (linkEnd > 0 && text[linkEnd - 1] == '\r') linkEnd--;
			int linkStart = linkEnd - link.Length;
			if (linkStart < 0 || !text.AsSpan(linkStart, link.Length).SequenceEqual(link)
				|| InsideCodeFence(text.AsSpan(0, linkStart))) continue;
			return IsInjectedUri(uri) ? linkStart : -1;
		}
		return -1;
	}

	private static bool InsideCodeFence(ReadOnlySpan<char> text) {
		char fence = '\0';
		int length = 0;
		foreach (var range in text.EnumerateLines()) {
			var line = range.TrimStart();
			if (line.Length < 3 || line[0] is not ('`' or '~')) continue;
			int count = 1;
			while (count < line.Length && line[count] == line[0]) count++;
			if (count < 3) continue;
			if (fence == '\0') {
				fence = line[0];
				length = count;
			} else if (line[0] == fence && count >= length && line[count..].Trim().IsEmpty) {
				fence = '\0';
			}
		}
		return fence != '\0';
	}
}
