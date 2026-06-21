using System.Text.Json;

namespace Weavie.Core.Changes;

/// <summary>
/// Builds the host→web JSON messages for the inline turn-review feed, so the Windows and macOS hosts emit
/// byte-identical payloads from one place (mirrored host glue, single source of truth). Each method returns
/// a complete JSON string ready for the host bridge's <c>PostToWeb</c>. The shapes mirror the web's
/// <c>WebBoundMessage</c> union in <c>src/web/src/bridge.ts</c>.
/// </summary>
public static class ChangeMessages {
	/// <summary>
	/// The per-<em>turn</em> change list: each file changed this turn with its added/removed line counts and the
	/// 1-based line of its first change, so the review navigator can open the file landed on that first diff.
	/// Pushed only in an auto-keep mode (acceptEdits/bypass) — the host gates it — since post-turn review is the
	/// review surface there; in default mode the blocking openDiff is. <paramref name="open"/> tells the page to
	/// auto-open the first file for review now: the host decides it (reading the session's status + change set
	/// together, so the decision is race-free) and the page just obeys — see the host's <c>ShouldOpenReview</c>.
	/// </summary>
	/// <param name="tracker">The session change tracker to summarize this turn from.</param>
	/// <param name="open">Whether the page should auto-open the first review file on receiving this list.</param>
	public static string TurnChanges(SessionChangeTracker tracker, bool open) {
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
		return JsonSerializer.Serialize(new { type = "turn-changes", open, files });
	}

	/// <summary>
	/// An empty turn-change list (never auto-opens). Pushed on a session switch into a session with no
	/// auto-applied turn changes (or one in <c>default</c> mode, where the openDiff is the review surface) so the
	/// page's ← / → review walk left over from the previous session is cleared rather than lingering over it.
	/// </summary>
	public static string EmptyTurnChanges() =>
		JsonSerializer.Serialize(new { type = "turn-changes", open = false, files = Array.Empty<object>() });

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
