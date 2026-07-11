namespace Weavie.Core.Sessions;

/// <summary>
/// Watches a single managed <c>claude</c> launch — its early output and eventual exit — so a session id that
/// can't be brought up (its transcript pruned or corrupt, or the id otherwise unusable) self-heals by being
/// forgotten (<see cref="ClaudeSessionStore.Forget"/>) instead of crash-looping the pane. Confirmation is gated
/// on output volume (a full terminal repaint), not its mere presence, so a launch that prints one error line and
/// dies is not mistaken for one that came up.
/// </summary>
public sealed class ClaudeStartupWatcher {
	// A launch that streams at least this much output has painted its TUI and is up. A startup failure is a
	// short line followed at once by exit, so it never reaches this threshold — no timer needed.
	private const int ConfirmAfterBytes = 4096;

	private int _seen;

	/// <summary>True once the launch has streamed enough output to be considered up (then output is ignored).</summary>
	public bool Confirmed { get; private set; }

	/// <summary>
	/// Feeds a chunk of decoded claude output. Returns <c>true</c> the single time the launch crosses the
	/// confirmation threshold.
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
	/// True when the launch exited without ever coming up — an unconfirmed non-zero exit, meaning its session id
	/// could not be brought up and should be forgotten so the next launch mints a fresh one. A confirmed launch
	/// or a clean exit (<paramref name="exitCode"/> 0 = the user quit) needs no healing.
	/// </summary>
	public bool FailedToStart(int exitCode) => !Confirmed && exitCode != 0;
}
