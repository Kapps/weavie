using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private bool _backgroundRunning;

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

	// Published inside the transition that changed the work, so the running flag reaches the status machine
	// before that transition's turn stop or completion does.
	private void PublishBackground() {
		lock (_turnTransitionGate) {
			var items = BackgroundWork;
			BackgroundWorkChanged?.Invoke(items);
			bool running = items.Any(item => item.Running);
			if (running == _backgroundRunning) return;
			_backgroundRunning = running;
			_context.Events.Observe(new AgentBackgroundChanged(running));
		}
	}
}
