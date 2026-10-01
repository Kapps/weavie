namespace Weavie.AgentClientProtocol;

/// <summary>Proves an ACP agent can launch and speaks the protocol, before Weavie relies on it.</summary>
public static class AcpAgentCheck {
	/// <summary>
	/// Starts <paramref name="definition"/> once as a throwaway process and completes the ACP <c>initialize</c>
	/// handshake; a package-runner agent (npx/uvx) downloads its package on this first start. Throws with the
	/// agent's own error output when it can't start or doesn't answer.
	/// </summary>
	public static async Task VerifyAsync(AcpAgentDefinition definition, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(definition);
		AcpTransientConnection connection;
		try {
			connection = AcpTransientConnection.Start(definition, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
		} catch (Exception ex) when (AcpTransientConnection.IsStartFailure(ex)) {
			throw new InvalidOperationException($"{definition.Name} could not be started: {ex.Message}", ex);
		}

		await using (connection) {
			try {
				connection.Listen((id, method, _) => connection.RefuseAsync(id, method), static (_, _) => { });
				await connection.RequestAsync("initialize", AcpInferenceClient.InitializeParameters, ct).ConfigureAwait(false);
			} catch (Exception ex) when (ex is IOException or AcpProtocolException or AcpAuthenticationRequiredException) {
				throw await connection.FailureAsync($"{definition.Name} started but didn't answer as an ACP agent", ex)
					.ConfigureAwait(false);
			}
		}
	}
}
