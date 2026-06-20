using System.Text;

namespace Weavie.Core.Sessions;

/// <summary>What a managed <c>claude</c> launch turned out to be, inferred from its early terminal output.</summary>
public enum ClaudeStartupOutcome {
	/// <summary>Not yet decided — keep feeding output.</summary>
	Pending,

	/// <summary>A <c>--session-id</c> (create) launch produced output: the session now exists (→ MarkStarted).</summary>
	Created,

	/// <summary>A <c>--resume</c> launch came up without the not-found error: the resume succeeded.</summary>
	Resumed,

	/// <summary>A <c>--resume</c> launch reported its id is missing ("No conversation found") (→ MarkResumeFailed).</summary>
	ResumeFailed,
}

/// <summary>
/// Watches the early stdout of a single managed <c>claude</c> launch to decide its outcome, so the
/// <see cref="ClaudeSessionStore"/>'s <c>Started</c> flag tracks reality rather than an optimistic guess: a
/// create that produces output is <see cref="ClaudeStartupOutcome.Created">confirmed</see>, while a resume that
/// prints <c>No conversation found with session ID</c> is
/// <see cref="ClaudeStartupOutcome.ResumeFailed">caught</see> so the supervisor's restart can re-create the id
/// instead of crash-looping on a resume that can never succeed. Fed the decoded PTY output; settles exactly once
/// (the first non-<see cref="ClaudeStartupOutcome.Pending"/> result) and ignores everything after. Pure +
/// synchronous, so it unit-tests without a real PTY.
/// </summary>
public sealed class ClaudeResumeWatcher {
	// Claude's wording is "No conversation found with session ID: <id>"; match the stable leading phrase.
	private const string ResumeFailedMarker = "No conversation found";

	// A resume that streams this much output without the marker has clearly come up — stop scanning. The marker
	// is printed immediately (then claude exits), so it always arrives well within this budget.
	private const int ResumeConfirmedAfter = 4096;

	private readonly bool _resuming;
	private readonly StringBuilder _tail = new();
	private bool _settled;

	/// <summary>
	/// Creates a watcher for a launch that used <c>--resume</c> (<paramref name="resuming"/> true) or
	/// <c>--session-id</c> (false).
	/// </summary>
	public ClaudeResumeWatcher(bool resuming) {
		_resuming = resuming;
	}

	/// <summary>
	/// Feeds a chunk of decoded claude output and returns the outcome the first time it can be decided, else
	/// <see cref="ClaudeStartupOutcome.Pending"/>. After it settles once, every later call returns <c>Pending</c>.
	/// </summary>
	public ClaudeStartupOutcome Observe(string output) {
		ArgumentNullException.ThrowIfNull(output);
		if (_settled) {
			return ClaudeStartupOutcome.Pending;
		}

		if (!_resuming) {
			// A create launch that's producing output has created the session.
			_settled = true;
			return ClaudeStartupOutcome.Created;
		}

		_tail.Append(output);
		if (_tail.ToString().Contains(ResumeFailedMarker, StringComparison.Ordinal)) {
			_settled = true;
			return ClaudeStartupOutcome.ResumeFailed;
		}

		if (_tail.Length >= ResumeConfirmedAfter) {
			_settled = true;
			return ClaudeStartupOutcome.Resumed;
		}

		return ClaudeStartupOutcome.Pending;
	}
}
