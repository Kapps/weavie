using System.IO.Pipes;
using System.Text;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// End-to-end over a real pipe: a request reaches the server, raises
/// <see cref="HookBridgeServer.Observed"/>, and pass-through returns an empty response.
/// </summary>
public sealed class HookBridgeServerTests {
	private static string UniquePipe() => $"weavie-hook-test-{Guid.NewGuid():N}";

	[Fact]
	public async Task PreToolUse_RaisesObserved_AndPassesThrough() {
		string pipe = UniquePipe();
		await using var server = new HookBridgeServer(pipe, decide: null);
		var observed = new TaskCompletionSource<HookRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
		server.Observed += request => observed.TrySetResult(request);
		server.Start();

		string payload = """{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"echo hi"}}""";
		byte[] response = await ExchangeAsync(pipe, Encoding.UTF8.GetBytes(payload));

		Assert.Empty(response); // pass-through → empty stdout
		var request = await WithTimeoutAsync(observed.Task);
		Assert.Equal("Bash", request.ToolName);
		Assert.Equal(HookEventKind.PreToolUse, request.Event);
	}

	[Fact]
	public async Task BypassDecider_ReturnsAllowDecision() {
		string pipe = UniquePipe();
		await using var server = new HookBridgeServer(pipe, _ => HookDecision.Allow("bypass"));
		server.Start();

		string payload = """{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"rm -rf x"}}""";
		byte[] response = await ExchangeAsync(pipe, Encoding.UTF8.GetBytes(payload));

		string json = Encoding.UTF8.GetString(response);
		Assert.Contains("\"permissionDecision\":\"allow\"", json, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HandlesConcurrentConnections_NotSerialized() {
		// The relay dials a fresh connection per hook, firing Pre/PostToolUse back-to-back; under load a
		// single-listener accept loop (accept → handle → dispose → re-listen) leaves a window where the next
		// connect races the re-bind and is reset, silently dropping a hook. The fix keeps a pool of bound
		// listeners. Assert that contract directly: hold several connections open at once and require all to be
		// in flight together before any completes — only possible with multiple listeners. A single-listener
		// loop can have just one connection in flight, so the rest never get accepted and the test times out.
		const int concurrent = 4; // the server's instance pool size
		string pipe = UniquePipe();
		var allInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int inFlight = 0;
		await using var server = new HookBridgeServer(pipe, _ => {
			if (Interlocked.Increment(ref inFlight) == concurrent) {
				allInFlight.TrySetResult();
			}

			allInFlight.Task.Wait(5000); // proceed only once every connection is concurrently accepted
			return HookDecision.PassThrough;
		});
		server.Start();

		byte[] payload = Encoding.UTF8.GetBytes(
			"""{"hook_event_name":"PostToolUse","tool_name":"Edit","tool_input":{"file_path":"f"}}""");
		var exchanges = new Task<byte[]>[concurrent];
		for (int i = 0; i < concurrent; i++) {
			exchanges[i] = ExchangeAsync(pipe, payload);
		}

		byte[][] responses = await WithTimeoutAsync(Task.WhenAll(exchanges));

		Assert.Equal(concurrent, responses.Length);
		Assert.All(responses, Assert.Empty); // every connection round-tripped (pass-through)
		Assert.Equal(concurrent, Volatile.Read(ref inFlight));
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
