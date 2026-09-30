namespace Weavie.AgentClientProtocol;

/// <summary>Proves an ACP agent can launch and speaks the protocol, before Weavie relies on it.</summary>
public static class AcpAgentCheck {
	private const int StderrTailLines = 12;

	/// <summary>
	/// Starts <paramref name="definition"/> once as a throwaway process and completes the ACP <c>initialize</c>
	/// handshake; a package-runner agent (npx/uvx) downloads its package on this first start. Throws with the
	/// agent's own error output when it can't start or doesn't answer.
	/// </summary>
	public static async Task VerifyAsync(AcpAgentDefinition definition, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(definition);
		var stderr = new Queue<string>();
		void Keep(string line) {
			lock (stderr) {
				stderr.Enqueue(line);
				if (stderr.Count > StderrTailLines) stderr.Dequeue();
			}
		}

		AcpTransientConnection connection;
		try {
			connection = AcpTransientConnection.Start(
				definition, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Keep);
		} catch (Exception ex) when (AcpTransientConnection.IsStartFailure(ex)) {
			throw new InvalidOperationException($"{definition.Name} could not be started: {ex.Message}", ex);
		}

		Exception failure;
		try {
			connection.Listen((id, method, _) => connection.RefuseAsync(id, method), static (_, _) => { });
			await connection.RequestAsync("initialize", AcpInferenceClient.InitializeParameters, ct).ConfigureAwait(false);
			return;
		} catch (Exception ex) when (ex is IOException or AcpProtocolException or AcpAuthenticationRequiredException) {
			failure = ex;
		} finally {
			await connection.DisposeAsync().ConfigureAwait(false);
		}

		// The agent's own explanation (e.g. an npm error) is on stderr, which can still be flushing after stdout closes.
		await connection.StderrDrained.ConfigureAwait(false);
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
