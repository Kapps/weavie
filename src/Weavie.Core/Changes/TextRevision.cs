using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Weavie.Core.Changes;

/// <summary>Content addresses for review texts: equal texts have equal revisions.</summary>
public static class TextRevision {
	// The tracker's texts are long-lived instances, so each is hashed once however often it is pushed.
	private static readonly ConditionalWeakTable<string, string> Revisions = [];

	/// <summary>The SHA-256 hex revision of <paramref name="text"/>.</summary>
	public static string Of(string text) {
		ArgumentNullException.ThrowIfNull(text);
		return Revisions.GetValue(text, static value => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))));
	}

	/// <summary>
	/// The revision of a version of a file: <see langword="null"/> when that version doesn't exist, so one comparison
	/// covers both content and existence.
	/// </summary>
	public static string? Of(string text, bool exists) => exists ? Of(text) : null;
}
