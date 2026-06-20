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

	/// <summary>
	/// The per-<em>turn</em> change list: each file changed this turn with its added/removed line counts and the
	/// 1-based line of its first change, so the review navigator can open the file landed on that first diff.
	/// Pushed only in an auto-keep mode (acceptEdits/bypass) — the host gates it — since post-turn review is the
	/// review surface there; in default mode the blocking openDiff is.
	/// </summary>
	/// <param name="tracker">The session change tracker to summarize this turn from.</param>
	public static string TurnChanges(SessionChangeTracker tracker) {
		ArgumentNullException.ThrowIfNull(tracker);
		var files = tracker.TurnChanges().Select(change => {
			var (added, removed) = LineDiff.Count(change.BaselineText, change.CurrentText);
			return new {
				path = change.Path,
				name = Path.GetFileName(change.Path),
				added,
				removed,
				line = LineDiff.FirstChangedLine(change.BaselineText, change.CurrentText) ?? 1,
			};
		});
		return JsonSerializer.Serialize(new { type = "turn-changes", files });
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
