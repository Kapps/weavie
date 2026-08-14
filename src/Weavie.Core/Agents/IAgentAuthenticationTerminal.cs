namespace Weavie.Core.Agents;

/// <summary>Presents one provider-declared interactive authentication invocation to the user.</summary>
public interface IAgentAuthenticationTerminal : IAsyncDisposable {
	/// <summary>Runs <paramref name="launch"/> in a visible terminal until it exits or is cancelled.</summary>
	Task<AgentProcessExit> RunAsync(AgentLaunch launch, CancellationToken ct);
}

/// <summary>An authentication terminal for hosts that cannot present an interactive process.</summary>
public sealed class UnavailableAgentAuthenticationTerminal : IAgentAuthenticationTerminal {
	/// <summary>The shared unavailable implementation.</summary>
	public static UnavailableAgentAuthenticationTerminal Instance { get; } = new();

	private UnavailableAgentAuthenticationTerminal() { }

	/// <inheritdoc/>
	public Task<AgentProcessExit> RunAsync(AgentLaunch launch, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(launch);
		ct.ThrowIfCancellationRequested();
		return Task.FromException<AgentProcessExit>(
			new NotSupportedException("This host cannot present ACP terminal authentication."));
	}

	/// <inheritdoc/>
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
