using System.IO.Pipes;
using System.Text;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// End-to-end over a real current-user-only pipe: the relay's request reaches the server, raises
/// <see cref="HookBridgeServer.Observed"/>, and the pass-through decision comes back as an empty response.
/// </summary>
public sealed class HookBridgeServerTests {
	private static string UniquePipe() => $"weavie-hook-test-{Guid.NewGuid():N}";

	[Fact]
	public async Task PreToolUse_RaisesObserved_AndPassesThrough() {
		string pipe = UniquePipe();
		await using var server = new HookBridgeServer(pipe);
		var observed = new TaskCompletionSource<HookRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
		server.Observed += request => observed.TrySetResult(request);
		server.Start();

		string payload = """{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"echo hi"}}""";
		byte[] response = await ExchangeAsync(pipe, Encoding.UTF8.GetBytes(payload));

		Assert.Empty(response); // pass-through → empty stdout → Claude's normal flow
		var request = await WithTimeoutAsync(observed.Task);
		Assert.Equal("Bash", request.ToolName);
		Assert.Equal(HookEventKind.PreToolUse, request.Event);
	}

	private static async Task<byte[]> ExchangeAsync(string pipe, byte[] payload) {
		using var client = new NamedPipeClientStream(
			".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await client.ConnectAsync(5000);
		await HookProtocol.WriteFramedAsync(client, payload, CancellationToken.None);
		return await HookProtocol.ReadFramedAsync(client, CancellationToken.None) ?? [];
	}

	private static async Task<T> WithTimeoutAsync<T>(Task<T> task) {
		var completed = await Task.WhenAny(task, Task.Delay(5000));
		Assert.True(completed == task, "observer was not raised within the timeout");
		return await task;
	}
}
