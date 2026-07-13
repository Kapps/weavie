using Weavie.Core.Coalescing;

namespace Weavie.Core.Agents;

/// <summary>
/// Batches a structured agent pane's live messages into fewer, larger bridge frames. One frame per pane message
/// overwhelms the headless bridge's bounded outbox on a burst — a fast turn, or a resumed thread replaying its
/// whole history, produces messages faster than a network-slow page drains them, so the page is dropped
/// mid-stream. Messages accumulate here and flush as one batch when the window elapses; a window of 0 posts each
/// message inline (as a one-element batch). See <see cref="CoalescerBase"/> for the shared window machinery.
/// </summary>
public sealed class AgentPaneCoalescer : CoalescerBase {
	private readonly Action<IReadOnlyList<AgentPaneMessage>> _post;
	private readonly List<AgentPaneMessage> _buffer = [];

	/// <summary>
	/// Coalesces messages posted through <paramref name="post"/> (the bridge sink), flushing after
	/// <paramref name="windowMs"/> milliseconds. <paramref name="windowMs"/> of 0 posts each message inline.
	/// </summary>
	public AgentPaneCoalescer(Action<IReadOnlyList<AgentPaneMessage>> post, long windowMs) : base(windowMs) {
		ArgumentNullException.ThrowIfNull(post);
		_post = post;
	}

	/// <summary>Buffers one live pane message, posting it inline when the window is disabled; otherwise arms the
	/// time-window flush. Runs on the provider thread; never blocks it.</summary>
	public void Add(AgentPaneMessage message) {
		ArgumentNullException.ThrowIfNull(message);
		lock (Gate) {
			if (IsDisposed) {
				return;
			}

			if (!WindowEnabled) {
				_post([message]);
				return;
			}

			_buffer.Add(message);
			ArmLocked();
		}
	}

	/// <inheritdoc/>
	protected override bool HasBuffered => _buffer.Count > 0;

	/// <inheritdoc/>
	protected override void ClearBuffer() => _buffer.Clear();

	/// <inheritdoc/>
	protected override void FlushBufferLocked() {
		var batch = _buffer.ToArray();
		_buffer.Clear();
		_post(batch);
	}
}
