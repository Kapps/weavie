using System.Text;

namespace Weavie.Core.Corrections;

/// <summary>
/// Byte-bounding shared by the ring's per-entry ceilings and the analysis prompt's context budget — both cut
/// text to fit a model window, so both cut it the same way.
/// </summary>
internal static class CorrectionText {
	/// <summary>Marks text a byte bound cut, so neither a reader nor the model mistakes a cut for the whole.</summary>
	public const string TruncationMarker = "…[truncated]";

	/// <summary>Cuts <paramref name="text"/> to at most <paramref name="maxBytes"/> of UTF-8 (never splitting a surrogate pair), marking the cut.</summary>
	public static string TruncateUtf8(string text, int maxBytes) {
		if (Encoding.UTF8.GetByteCount(text) <= maxBytes) {
			return text;
		}

		int budget = maxBytes - Encoding.UTF8.GetByteCount(TruncationMarker);
		int bytes = 0;
		int end = 0;
		for (int i = 0; i < text.Length;) {
			bool pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
			int size = pair ? 4 : Encoding.UTF8.GetByteCount(text, i, 1);
			if (bytes + size > budget) {
				break;
			}

			bytes += size;
			i += pair ? 2 : 1;
			end = i;
		}

		return text[..end] + TruncationMarker;
	}
}
