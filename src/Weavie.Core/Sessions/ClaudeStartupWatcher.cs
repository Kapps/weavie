namespace Weavie.Core.Sessions;

/// <summary>How to reconcile <see cref="ClaudeSessionStore"/> after a managed <c>claude</c> launch has ended.</summary>
public enum ClaudeStartupRecovery {
	/// <summary>Nothing to heal — the launch came up, or exited cleanly (the user quit it).</summary>
	None,

	/// <summary>
	/// A <c>--resume</c> died at startup (its conversation is gone): re-create the same id with
	/// <c>--session-id</c> (<see cref="ClaudeSessionStore.MarkResumeFailed"/>), keeping the directory's identity.
	/// </summary>
	RecreateSameId,

	/// <summary>
	/// A <c>--session-id</c> create died at startup: the id itself is poison and can't be re-created, so forget
	/// it (<see cref="ClaudeSessionStore.Forget"/>) and let the next launch mint a fresh one.
	/// </summary>
	ForgetId,
}

/// <summary>
/// Watches a single managed <c>claude</c> launch — its early output <em>and</em> its eventual exit — to keep
/// the <see cref="ClaudeSessionStore"/>'s <c>Started</c> flag honest, so a dead or poison session id self-heals
/// instead of crash-looping the pane. A launch is <see cref="Confirmed">confirmed</see> up once it has streamed
/// a full terminal repaint (the TUI is large; a startup error is tiny and is followed immediately by exit) — at
/// which point the caller marks it started so the next launch can <c>--resume</c> it. If instead it exits before
/// confirming, <see cref="OnExit"/> reports how to heal: a failed resume re-creates the same id, a failed create
/// forgets the poison id. Crucially, confirmation is gated on <em>volume of output</em>, never on the mere
/// presence of output — a create that prints one error line and dies must not be mistaken for one that came up.
/// Pure + synchronous (output content and exit code only, no PTY), so it unit-tests in isolation.
/// </summary>
public sealed class ClaudeStartupWatcher {
	// A launch that streams at least this much output has painted its TUI and is up. A startup failure
	// ("No conversation found", an id collision, "command not found") is a short line followed at once by exit,
	// so it never reaches this — the gap between the two is wide enough to tell them apart without a timer.
	private const int ConfirmAfterBytes = 4096;

	private readonly bool _resuming;
	private int _seen;

	/// <summary>Creates a watcher for a launch that used <c>--resume</c> (<paramref name="resuming"/> true) or <c>--session-id</c> (false).</summary>
	public ClaudeStartupWatcher(bool resuming) {
		_resuming = resuming;
	}

	/// <summary>True once the launch has streamed enough output to be considered up (then output is ignored).</summary>
	public bool Confirmed { get; private set; }

	/// <summary>
	/// Feeds a chunk of decoded claude output. Returns <c>true</c> the single time the launch crosses the
	/// confirmation threshold (so the caller marks the session started); <c>false</c> otherwise, including every
	/// call once already <see cref="Confirmed"/>.
	/// </summary>
	public bool Observe(string output) {
		ArgumentNullException.ThrowIfNull(output);
		if (Confirmed) {
			return false;
		}

		_seen += output.Length;
		if (_seen < ConfirmAfterBytes) {
			return false;
		}

		Confirmed = true;
		return true;
	}

	/// <summary>
	/// Decides how to heal the store now that the launch has exited with <paramref name="exitCode"/>. A
	/// confirmed launch — or any clean exit (code 0 = the user quit) — needs nothing; an unconfirmed crash is a
	/// failed startup, healed per whether it was a resume or a create.
	/// </summary>
	public ClaudeStartupRecovery OnExit(int exitCode) {
		if (Confirmed || exitCode == 0) {
			return ClaudeStartupRecovery.None;
		}

		return _resuming ? ClaudeStartupRecovery.RecreateSameId : ClaudeStartupRecovery.ForgetId;
	}
}
