using System.Text.Json;

namespace Weavie.Core.Changes;

/// <summary>
/// Builds the host→web JSON messages for the inline turn-review feed so every host emits byte-identical
/// payloads; shapes mirror the web's <c>WebBoundMessage</c> union in <c>src/web/src/bridge.ts</c>.
/// </summary>
public static class ChangeMessages {
	/// <summary>
	/// The per-turn change list: each changed file with its added/removed counts and the 1-based line of its
	/// first change, so the navigator can open the file on that diff. <paramref name="open"/> auto-opens the first file.
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
	/// An empty turn-change list (never auto-opens). Pushed on a session switch to clear the prior session's ← / → walk.
	/// </summary>
	public static string EmptyTurnChanges() =>
		JsonSerializer.Serialize(new { type = "turn-changes", open = false, files = Array.Empty<object>() });

	/// <summary>
	/// One file's turn diff (baseline vs. current) for the inline editor diff. An equal pair means "no markers".
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
