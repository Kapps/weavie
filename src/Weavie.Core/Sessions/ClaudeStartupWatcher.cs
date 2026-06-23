namespace Weavie.Core.Sessions;

/// <summary>How to reconcile <see cref="ClaudeSessionStore"/> after a managed <c>claude</c> launch has ended.</summary>
public enum ClaudeStartupRecovery {
	/// <summary>Nothing to heal — the launch came up, or exited cleanly (the user quit it).</summary>
	None,

	/// <summary>
	/// A <c>--resume</c> died at startup: re-create the same id, keeping the directory's identity
	/// (<see cref="ClaudeSessionStore.MarkResumeFailed"/>).
	/// </summary>
	RecreateSameId,

	/// <summary>
	/// A <c>--session-id</c> create died at startup: the id is poison, so forget it and mint fresh next launch
	/// (<see cref="ClaudeSessionStore.Forget"/>).
	/// </summary>
	ForgetId,
}

/// <summary>
/// Watches a single managed <c>claude</c> launch — its early output and eventual exit — to keep the
/// <see cref="ClaudeSessionStore"/>'s <c>Started</c> flag honest, so a dead or poison session id self-heals
/// instead of crash-looping the pane. Confirmation is gated on output volume (a full terminal repaint), not
/// its mere presence, so a create that prints one error line and dies is not mistaken for one that came up.
/// </summary>
public sealed class ClaudeStartupWatcher {
	// A launch that streams at least this much output has painted its TUI and is up. A startup failure is a
	// short line followed at once by exit, so it never reaches this threshold — no timer needed.
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
	/// confirmation threshold (so the caller marks the session started).
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
	/// Decides how to heal the store now that the launch exited with <paramref name="exitCode"/>: a confirmed
	/// launch or clean exit needs nothing; an unconfirmed crash heals per whether it was a resume or a create.
	/// </summary>
	public ClaudeStartupRecovery OnExit(int exitCode) {
		if (Confirmed || exitCode == 0) {
			return ClaudeStartupRecovery.None;
		}

		return _resuming ? ClaudeStartupRecovery.RecreateSameId : ClaudeStartupRecovery.ForgetId;
	}
}
