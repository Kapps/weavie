using System.Collections.Concurrent;
using System.Threading.Channels;
using Weavie.Core.Agents;

namespace Weavie.Hosting.Tests;

internal sealed class RecordingConversationOutput(string providerId) : IConversationOutputSink {
	public ConcurrentQueue<AgentPaneMessage> History { get; } = new();
	public Channel<AgentPaneMessage> Messages { get; } = Channel.CreateUnbounded<AgentPaneMessage>();
	public Channel<bool> Resets { get; } = Channel.CreateUnbounded<bool>();
	public ConcurrentQueue<AgentBlockRecord> Blocks { get; } = new();

	public void Live(AgentPaneRecord record) {
		History.Enqueue(record.Message);
		Messages.Writer.TryWrite(record.Message);
	}

	public void Block(AgentBlockRecord record) => Blocks.Enqueue(record);

	public void Reset() {
		var message = new AgentPaneMessage { Type = "transcript-reset", ProviderId = providerId };
		History.Enqueue(message);
		Messages.Writer.TryWrite(message);
		Resets.Writer.TryWrite(true);
	}
}
