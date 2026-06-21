using System.Text.Json;

namespace Weavie.Core.Changes;

/// <summary>
/// Builds the host→web JSON messages for the inline turn-review feed, so every host emits byte-identical
/// payloads from one place. Each method returns a complete JSON string ready for the host bridge's
/// <c>PostToWeb</c>; the shapes mirror the web's <c>WebBoundMessage</c> union in <c>src/web/src/bridge.ts</c>.
/// </summary>
public static class ChangeMessages {
	/// <summary>
	/// The per-turn change list: each file changed this turn with its added/removed line counts and the 1-based
	/// line of its first change, so the review navigator can open the file landed on that first diff. Built from
	/// the change tracker, which records edits in every permission mode, so this is the review surface in all
	/// modes (default included). <paramref name="open"/> tells the page to auto-open the first file now.
	/// </summary>
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
	/// auto-applied turn changes, so the previous session's ← / → review walk is cleared rather than lingering.
	/// </summary>
	public static string EmptyTurnChanges() =>
		JsonSerializer.Serialize(new { type = "turn-changes", open = false, files = Array.Empty<object>() });

	/// <summary>
	/// One file's turn diff (this turn's baseline vs. current), for the inline diff in the live editor. An equal
	/// baseline/current pair means "no markers" (the file was accepted or reverted this turn).
	/// </summary>
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
