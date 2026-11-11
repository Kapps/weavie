using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private bool _backgroundRunning;
	private int _backgroundQueued;
	private readonly Lock _backgroundGate = new();

	/// <inheritdoc/>
	public event Action<IReadOnlyList<AgentBackgroundItem>>? BackgroundWorkChanged;

	/// <inheritdoc/>
	public IReadOnlyList<AgentBackgroundItem> BackgroundWork => _runtime.Background;

	/// <inheritdoc/>
	public Task<bool> StopBackgroundTaskAsync(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		foreach (var root in _runtime.Conversations.Where(conversation => conversation.OwnsWork)) {
			if (root.Background.StoppableTask(id) is { } task) return root.StopTaskAsync(task);
		}
		throw new InvalidOperationException("That background work cannot be stopped.");
	}

	private AcpConversation[] Roots() {
		lock (_turnTransitionGate) return [_primary, .. _sides.Values.Select(side => side.Conversation), .. _runtime.Conversations.Where(conversation => conversation.Independent)];
	}

	private AcpConversation? NestedConversation(string conversationId) =>
		_runtime.Conversations.FirstOrDefault(conversation => conversation.ConversationId == conversationId);

	private void QueueBackground() {
		if (Interlocked.Exchange(ref _backgroundQueued, 1) != 0) return;
		ThreadPool.QueueUserWorkItem(_ => {
			Interlocked.Exchange(ref _backgroundQueued, 0);
			PublishBackground();
		});
	}

	private void PublishBackground() {
		lock (_backgroundGate) {
			var items = BackgroundWork;
			BackgroundWorkChanged?.Invoke(items);
			bool running = items.Any(item => item.Running);
			if (running == _backgroundRunning) return;
			_backgroundRunning = running;
			_context.Events.Observe(new AgentBackgroundChanged(running));
		}
	}
}
