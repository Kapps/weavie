using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Hosting.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class AgentPaneHistoryStreamTests {
	[Fact]
	public async Task Unloading_the_owner_cancels_its_blocked_history_response() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("stream-owner")).Ok);
		var owner = host.SelectedSession.Address;
		host.SelectWorkspaceSession();
		using var output = new BlockedFlushStream();
		var writing = host.Core.WriteAgentHistoryAsync(owner, new(null, null), output, CancellationToken.None);
		await output.Flushing.Task;
		Assert.False(writing.IsCompleted);
		Assert.True((await host.UnloadSessionAsync(owner.Slot)).Ok);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task History_waits_for_its_consumer_and_cancels_without_writing_completion(bool cancel) {
		var records = Enumerable.Range(0, 100).Select(index => new AgentPaneRecord(1, index, index,
			new AgentPaneMessage { Type = "item-completed", ProviderId = "test", Text = $"message {index}" })).ToArray();
		using var output = new BlockedFlushStream();
		using var cancellation = new CancellationTokenSource();
		var writing = AgentPaneProtocol.WriteHistoryAsync(new(1, 100, records), output, cancellation.Token);
		await output.Flushing.Task;
		Assert.False(writing.IsCompleted);
		string firstBatch = Encoding.UTF8.GetString(output.ToArray());
		var first = JsonSerializer.Deserialize<JsonElement>(firstBatch);
		Assert.False(first.GetProperty("complete").GetBoolean());
		Assert.Equal("message 99", first.GetProperty("messages").EnumerateArray().Last().GetProperty("text").GetString());

		if (cancel) {
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
			Assert.Equal(firstBatch, Encoding.UTF8.GetString(output.ToArray()));
		} else {
			output.Release.SetResult();
			await writing;
			string[] batches = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
			Assert.True(JsonSerializer.Deserialize<JsonElement>(batches[^1]).GetProperty("complete").GetBoolean());
		}
	}

	private sealed class BlockedFlushStream : MemoryStream {
		public TaskCompletionSource Flushing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override async Task FlushAsync(CancellationToken cancellationToken) {
			Flushing.TrySetResult();
			await Release.Task.WaitAsync(cancellationToken);
		}
	}
}
