using System.Security.Cryptography;
using System.Text;

namespace Weavie.Core.Changes;

internal static class ReviewTextCodec {
	public static ReviewTextCheckpoint Encode(string anchor, string target) => new() {
		Hash = Hash(target),
		Splices = CreateSplices(anchor, target),
	};

	public static string Decode(string anchor, ReviewTextCheckpoint checkpoint) {
		var ordered = checkpoint.Splices.OrderBy(value => value.Offset).ToList();
		int previousEnd = 0;
		for (int index = 0; index < ordered.Count; index++) {
			var splice = ordered[index];
			if (splice.Offset < 0
				|| splice.DeleteLength < 0
				|| splice.Offset > anchor.Length
				|| splice.DeleteLength > anchor.Length - splice.Offset
				|| index > 0 && (splice.Offset < previousEnd || splice.Offset == ordered[index - 1].Offset)) {
				throw new InvalidDataException("Review checkpoint contains overlapping or invalid text splices.");
			}
			previousEnd = splice.Offset + splice.DeleteLength;
		}

		var text = new StringBuilder(anchor);
		foreach (var splice in ordered.AsEnumerable().Reverse()) {
			text.Remove(splice.Offset, splice.DeleteLength);
			text.Insert(splice.Offset, splice.InsertText);
		}

		string result = text.ToString();
		if (!string.Equals(Hash(result), checkpoint.Hash, StringComparison.Ordinal)) {
			throw new InvalidDataException("Review checkpoint text failed its integrity check.");
		}

		return result;
	}

	public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	private static List<ReviewSplice> CreateSplices(string source, string target) {
		if (string.Equals(source, target, StringComparison.Ordinal)) {
			return [];
		}

		var sourceLines = RawLines(source);
		var targetLines = RawLines(target);
		int[] sourceOffsets = Offsets(sourceLines);
		int[] targetOffsets = Offsets(targetLines);
		var splices = new List<ReviewSplice>();
		foreach (var hunk in LineHunker.Hunks(sourceLines, targetLines)) {
			int sourceStart = sourceOffsets[hunk.BeforeRange.Start - 1];
			int sourceEnd = sourceOffsets[hunk.BeforeRange.EndExclusive - 1];
			int targetStart = targetOffsets[hunk.AfterRange.Start - 1];
			int targetEnd = targetOffsets[hunk.AfterRange.EndExclusive - 1];
			splices.Add(new ReviewSplice {
				Offset = sourceStart,
				DeleteLength = sourceEnd - sourceStart,
				InsertText = target[targetStart..targetEnd],
			});
		}

		return splices;
	}

	private static List<string> RawLines(string text) {
		var lines = new List<string>();
		int start = 0;
		for (int index = 0; index < text.Length; index++) {
			if (text[index] == '\r') {
				if (index + 1 < text.Length && text[index + 1] == '\n') {
					index++;
				}
			} else if (text[index] != '\n') {
				continue;
			}

			lines.Add(text[start..(index + 1)]);
			start = index + 1;
		}

		if (start < text.Length) {
			lines.Add(text[start..]);
		}

		return lines;
	}

	private static int[] Offsets(IReadOnlyList<string> lines) {
		int[] offsets = new int[lines.Count + 1];
		for (int index = 0; index < lines.Count; index++) {
			offsets[index + 1] = offsets[index] + lines[index].Length;
		}
		return offsets;
	}
}
