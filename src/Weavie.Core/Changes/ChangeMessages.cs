using System.Text.Json;

namespace Weavie.Core.Changes;

/// <summary>
/// Builds the host→web JSON messages for the session-changes feed, so the Windows and macOS hosts emit
/// byte-identical payloads from one place (mirrored host glue, single source of truth). Each method returns
/// a complete JSON string ready for the host bridge's <c>PostToWeb</c>. The shapes mirror the web's
/// <c>WebBoundMessage</c> union in <c>src/web/src/bridge.ts</c>.
/// </summary>
public static class ChangeMessages {
	/// <summary>The change list: each tracked file's path, display name, and added/removed line counts.</summary>
	/// <param name="tracker">The session change tracker to summarize.</param>
	public static string SessionChanges(SessionChangeTracker tracker) {
		ArgumentNullException.ThrowIfNull(tracker);
		var files = tracker.Summarize().Select(change => new {
			path = change.Path,
			name = Path.GetFileName(change.Path),
			added = change.Added,
			removed = change.Removed,
		});
		return JsonSerializer.Serialize(new { type = "session-changes", files });
	}

	/// <summary>One file's session diff: its baseline (content at first touch) vs. its current content.</summary>
	/// <param name="change">The file change to serialize.</param>
	public static string ChangeDiff(FileChange change) {
		ArgumentNullException.ThrowIfNull(change);
		return JsonSerializer.Serialize(new {
			type = "change-diff",
			path = change.Path,
			name = Path.GetFileName(change.Path),
			baseline = change.BaselineText,
			current = change.CurrentText,
		});
	}

	/// <summary>A live-refresh push: replace the open editor model for <paramref name="path"/> with <paramref name="content"/>.</summary>
	/// <param name="path">Absolute path of the file Claude edited.</param>
	/// <param name="content">The file's new content (read from disk after the edit landed).</param>
	public static string RefreshFile(string path, string content) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(content);
		return JsonSerializer.Serialize(new { type = "refresh-file", path, content });
	}

	/// <summary>
	/// One file's <em>turn</em> diff (this turn's baseline vs. current), for the inline diff in the live editor.
	/// An equal baseline/current pair means "no markers" (the file was accepted or reverted this turn).
	/// </summary>
	/// <param name="change">The per-turn file change to serialize.</param>
	public static string TurnDiff(FileChange change) {
		ArgumentNullException.ThrowIfNull(change);
		return JsonSerializer.Serialize(new {
			type = "turn-diff",
			path = change.Path,
			name = Path.GetFileName(change.Path),
			baseline = change.BaselineText,
			current = change.CurrentText,
		});
	}

	/// <summary>A turn boundary: the page clears all inline turn markers (the prior turn is implicitly accepted).</summary>
	public static string TurnReset() => JsonSerializer.Serialize(new { type = "turn-reset" });
}
