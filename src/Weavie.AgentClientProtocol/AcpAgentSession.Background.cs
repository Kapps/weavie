using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private bool _backgroundRunning;

	/// <inheritdoc/>
	public event Action<IReadOnlyList<AgentBackgroundItem>>? BackgroundWorkChanged;

	/// <inheritdoc/>
	public IReadOnlyList<AgentBackgroundItem> BackgroundWork => [.. Roots().SelectMany(root => root.Background.Items)];

	/// <inheritdoc/>
	public Task<bool> StopBackgroundTaskAsync(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		throw new InvalidOperationException("That background work cannot be stopped.");
	}

	private AcpConversation[] Roots() {
		lock (_gate) return [_primary, .. _sides.Values.Select(side => side.Conversation)];
	}

	private AcpConversation? NestedConversation(string conversationId) {
		lock (_gate) if (_sides.TryGetValue(conversationId, out var side)) return side.Conversation;
		return Roots().Select(root => root.Background.Conversation(conversationId)).FirstOrDefault(conversation => conversation is not null);
	}

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
