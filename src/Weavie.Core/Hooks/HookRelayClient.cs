using System.IO.Pipes;

namespace Weavie.Core.Hooks;

/// <summary>
/// The relay side of the hook bridge: the program Claude runs as its <c>command</c> hook. Reads the hook JSON
/// from stdin, forwards it to the in-process <c>HookBridgeServer</c> over the pipe named by
/// <see cref="HookProtocol.PipeEnvVar"/>, and writes the decision (if any) back to stdout for Claude. Fails
/// OPEN: any error (no pipe, no server, timeout) exits 0 with no stdout, so Claude proceeds through its normal
/// flow — the bridge never blocks a tool because the recorder hiccupped. It lives in the standalone <c>Weavie.HookRelay</c> executable (which links this file),
/// co-located with the host by the build (Debug managed, Release NativeAOT).
/// </summary>
public static class HookRelayClient {
	private const int ConnectTimeoutMs = 5000;

	/// <summary>Runs one relay exchange and returns the process exit code (always 0 — fail-open).</summary>
	public static int Run() {
		string? pipeName = Environment.GetEnvironmentVariable(HookProtocol.PipeEnvVar);
		if (string.IsNullOrEmpty(pipeName)) {
			return 0;
		}

		try {
			byte[] payload = ReadAllStdin();
			byte[] response = ExchangeAsync(pipeName, payload).GetAwaiter().GetResult();
			if (response.Length > 0) {
				using var stdout = Console.OpenStandardOutput();
				stdout.Write(response, 0, response.Length);
				stdout.Flush();
			}
		} catch {
			// Fail open: never let a relay error block Claude's tool call.
		}

		return 0;
	}

	private static byte[] ReadAllStdin() {
		using var stdin = Console.OpenStandardInput();
		using var memory = new MemoryStream();
		stdin.CopyTo(memory);
		return memory.ToArray();
	}

	private static async Task<byte[]> ExchangeAsync(string pipeName, byte[] payload) {
		using var client = new NamedPipeClientStream(
			".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await client.ConnectAsync(ConnectTimeoutMs).ConfigureAwait(false);
		await HookProtocol.WriteFramedAsync(client, payload, CancellationToken.None).ConfigureAwait(false);
		return await HookProtocol.ReadFramedAsync(client, CancellationToken.None).ConfigureAwait(false) ?? [];
	}
}
