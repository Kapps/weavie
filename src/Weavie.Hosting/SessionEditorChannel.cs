namespace Weavie.Hosting;

/// <summary>
/// The per-session gate for editor-mutating page messages — openDiff's <c>show-diff</c>, openFile's
/// <c>open-file</c>, and close_tab's <c>close-tab</c>. The page has exactly ONE editor surface, bound to the
/// active session; a background session must never write into it. Each session's openDiff/openFile/close_tab
/// routes through its own channel, which posts to the page only while the session is active and otherwise HOLDS
/// the work — replaying it on switch-in. So a background Claude's blocking openDiff surfaces on switch-in rather
/// than rendering over the foreground session or hanging unseen. Mirrors <see cref="TerminalController"/>'s
/// <c>OutputActive</c> buffer-or-post muting for the terminals.
///
/// Created muted: a session drives the editor only once HostCore makes it active (see
/// <see cref="HostSession.SetEditorOutputActive"/>), so a never-activated background slot can't contaminate the
/// page even if a creation path forgets to mute it.
/// </summary>
public sealed class SessionEditorChannel {
	private readonly IHostBridge _bridge;
	private readonly object _gate = new();
	// open-file / close-tab posted while muted, replayed in order on activation. Fire-and-forget — only order
	// matters.
	private readonly List<string> _pendingReveals = [];
	// The session's single unresolved openDiff (it blocks, so at most one is live at a time): the show-diff
	// payload is HELD so a switch away can tear it out of the page and a switch back can re-render it, until the
	// user resolves it. Null when no diff is pending.
	private string? _liveDiffId;
	private string? _liveDiffShow;
	private bool _active;

	/// <summary>Creates a muted channel that posts to the page through <paramref name="bridge"/> once activated.</summary>
	public SessionEditorChannel(IHostBridge bridge) {
		ArgumentNullException.ThrowIfNull(bridge);
		_bridge = bridge;
	}

	/// <summary>
	/// Posts a fire-and-forget editor message (open-file / close-tab) if this session is active, else buffers it
	/// for replay when the session is next switched in.
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
	/// deactivate can tear it down and a re-activate can re-render it; <see cref="EndDiff"/> stops tracking it
	/// once the diff resolves or is cancelled.
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
	/// Stops tracking the openDiff <paramref name="id"/> (it resolved or was cancelled). When
	/// <paramref name="closeInUi"/> and the session is active, also tears the rendered diff out of the page — used
	/// on cancellation; a user's Keep/Reject already closed it in the page itself, so that path passes
	/// <c>false</c>.
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
	/// Makes this the editor-driving session: flushes any buffered reveals (in order), then re-renders a held
	/// blocking diff. Idempotent — a no-op if already active. Called by HostCore when the session is switched in,
	/// after the editor has been rebound to this session's tabs, so the replayed messages land on the right slot.
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
	/// Mutes this session (it's no longer the editor-driving one): a held blocking diff is torn out of the page
	/// so it can't linger over the incoming session — it re-renders on the next <see cref="Activate"/>.
	/// Idempotent — a no-op if already muted.
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
