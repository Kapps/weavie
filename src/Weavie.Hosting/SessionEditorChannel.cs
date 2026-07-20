namespace Weavie.Hosting;

/// <summary>
/// The per-session gate for editor-mutating page messages (show-diff/open-file/close-tab). The page has one
/// editor surface, bound to the active session, so each session's channel posts only while active and otherwise
/// HOLDS the work — replaying it on switch-in (a background openDiff surfaces then, not over the foreground).
/// Created muted, so a never-activated background slot can't contaminate the page.
/// </summary>
public sealed class SessionEditorChannel {
	private readonly IHostBridge _bridge;
	private readonly object _gate = new();
	// open-file / close-tab posted while muted, replayed in order on activation.
	private readonly List<string> _pendingReveals = [];
	// The session's single unresolved openDiff (it blocks, so at most one): the show-diff payload is held so a
	// switch away tears it out and a switch back re-renders it. Null when no diff is pending.
	private string? _liveDiffId;
	private string? _liveDiffShow;
	private bool _active;

	/// <summary>Creates a muted channel that posts to the page through <paramref name="bridge"/> once activated.</summary>
	public SessionEditorChannel(IHostBridge bridge) {
		ArgumentNullException.ThrowIfNull(bridge);
		_bridge = bridge;
	}

	/// <summary>
	/// Posts a fire-and-forget editor message (open-file / close-tab / focus-omnibar) if this session is active,
	/// else buffers it for replay when the session is next switched in.
	/// </summary>
	public void Reveal(string message) {
		lock (_gate) {
			if (!_active) {
				_pendingReveals.Add(message);
				return;
			}
		}

		_bridge.PostToWeb(message);
	}

	/// <summary>
	/// Tracks (and, when active, renders) the session's single blocking openDiff. The payload is held so a
	/// deactivate can tear it down and a re-activate re-render it; <see cref="EndDiff"/> stops tracking it.
	/// </summary>
	public void ShowDiff(string id, string showMessage) {
		lock (_gate) {
			_liveDiffId = id;
			_liveDiffShow = showMessage;
			if (!_active) {
				return;
			}
		}

		_bridge.PostToWeb(showMessage);
	}

	/// <summary>
	/// Stops tracking the openDiff <paramref name="id"/> (resolved or cancelled). With <paramref name="closeInUi"/>
	/// and the session active, also tears the rendered diff out of the page (cancellation only — a user's
	/// Keep/Reject already closed it, so that path passes <c>false</c>).
	/// </summary>
	public void EndDiff(string id, bool closeInUi) {
		string? close = null;
		lock (_gate) {
			if (_liveDiffId != id) {
				return;
			}

			_liveDiffId = null;
			_liveDiffShow = null;
			if (_active && closeInUi) {
				close = CloseDiff(id);
			}
		}

		if (close is not null) {
			_bridge.PostToWeb(close);
		}
	}

	/// <summary>
	/// Makes this the editor-driving session: flushes buffered reveals (in order), then re-renders a held blocking
	/// diff. Idempotent. Called after the editor is rebound to this session's tabs, so replays land on the right slot.
	/// </summary>
	public void Activate() {
		List<string> reveals;
		string? show;
		lock (_gate) {
			if (_active) {
				return;
			}

			_active = true;
			reveals = [.. _pendingReveals];
			_pendingReveals.Clear();
			show = _liveDiffShow;
		}

		foreach (string message in reveals) {
			_bridge.PostToWeb(message);
		}

		if (show is not null) {
			_bridge.PostToWeb(show);
		}
	}

	/// <summary>
	/// Re-renders the active session's held openDiff to a just-(re)connected page. The diff is posted once when it
	/// arrives, so a page that connects after that — a reload, or a slow first connect under load — never saw it
	/// (<see cref="Activate"/> would re-render it but is idempotent and a no-op on the already-active session). A
	/// no-op when inactive (a background session's diff must not surface over the foreground) or none is pending.
	/// </summary>
	public void Replay() {
		string? show;
		lock (_gate) {
			if (!_active) {
				return;
			}

			show = _liveDiffShow;
		}

		if (show is not null) {
			_bridge.PostToWeb(show);
		}
	}

	/// <summary>
	/// Mutes this session: a held blocking diff is torn out of the page so it can't linger over the incoming
	/// session (it re-renders on the next <see cref="Activate"/>). Idempotent.
	/// </summary>
	public void Deactivate() {
		string? close = null;
		lock (_gate) {
			if (!_active) {
				return;
			}

			_active = false;
			if (_liveDiffId is not null) {
				close = CloseDiff(_liveDiffId);
			}
		}

		if (close is not null) {
			_bridge.PostToWeb(close);
		}
	}

	// The id is a safe slug (diff-N), so it needs no JSON escaping — matches McpDiffPresenter's show-diff id.
	private static string CloseDiff(string id) => $"{{\"type\":\"close-diff\",\"id\":\"{id}\"}}";
}
