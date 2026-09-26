using System.ComponentModel;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpInferenceClient {
	private const int StderrTailLines = 12;

	/// <summary>Starts the agent once and completes the ACP initialize handshake, or throws why it couldn't.</summary>
	internal static async Task HandshakeAsync(AcpAgentDefinition definition, CancellationToken ct) {
		var stderr = new Queue<string>();
		void Keep(string line) {
			lock (stderr) {
				stderr.Enqueue(line);
				if (stderr.Count > StderrTailLines) stderr.Dequeue();
			}
		}

		AcpInferenceClient client;
		try {
			client = Start(definition, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Keep);
		} catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or IOException
			or InvalidOperationException or UnauthorizedAccessException) {
			throw new InvalidOperationException($"{definition.Name} could not be started: {ex.Message}", ex);
		}

		Exception failure;
		try {
			await client.RequestAsync("initialize", InitializeParameters, ct).ConfigureAwait(false);
			return;
		} catch (Exception ex) when (ex is IOException or AcpProtocolException or AcpAuthenticationRequiredException) {
			failure = ex;
		} finally {
			await client.DisposeAsync().ConfigureAwait(false);
		}

		// The agent's own explanation (e.g. an npm error) is on stderr, which can still be flushing after stdout closes.
		await client._stderrDrained.ConfigureAwait(false);
		string output;
		lock (stderr) {
			output = string.Join('\n', stderr).Trim();
		}
		throw new InvalidOperationException(
			$"{definition.Name} started but didn't answer as an ACP agent: {failure.Message}"
				+ (output.Length > 0 ? $"\n{output}" : string.Empty),
			failure);
	}
}

/// <summary>Proves an ACP agent can launch and speaks the protocol, before Weavie relies on it.</summary>
public static class AcpAgentCheck {
	/// <summary>
	/// Starts <paramref name="definition"/> once as a throwaway process and completes the ACP <c>initialize</c>
	/// handshake; a package-runner agent (npx/uvx) downloads its package on this first start. Throws with the
	/// agent's own error output when it can't start or doesn't answer.
	/// </summary>
	public static Task VerifyAsync(AcpAgentDefinition definition, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(definition);
		return AcpInferenceClient.HandshakeAsync(definition, ct);
	}
}
