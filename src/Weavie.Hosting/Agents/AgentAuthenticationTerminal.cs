using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Processes;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Agents;

internal sealed class AgentAuthenticationTerminal : IAgentAuthenticationTerminal, ITerminalProcess {
	private readonly MessageFeatureChannel _agentMessages;
	private readonly Lock _gate = new();
	private AgentLaunch? _launch;
	private TaskCompletionSource<AgentProcessExit>? _completion;

	public AgentAuthenticationTerminal(
		MessageFeatureChannel agentMessages,
		MessageFeatureChannel terminalMessages,
		SettingsStore settings,
		IPtyLauncher launcher,
		string workspace,
		string scrollbackPath) {
		_agentMessages = agentMessages ?? throw new ArgumentNullException(nameof(agentMessages));
		ArgumentNullException.ThrowIfNull(terminalMessages);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(launcher);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		ArgumentException.ThrowIfNullOrEmpty(scrollbackPath);
		Controller = new TerminalController(
			terminalMessages,
			"agent-authentication",
			settings,
			launcher,
			this,
			RestartPolicy.Never) {
			Workspace = workspace,
			ScrollbackLogPath = scrollbackPath,
		};
		Controller.SupervisorChanged += ObserveSupervisor;
	}

	public TerminalController Controller { get; }

	public bool Active {
		get {
			lock (_gate) return _completion is not null;
		}
	}

	public async Task<AgentProcessExit> RunAsync(AgentLaunch launch, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(launch);
		ct.ThrowIfCancellationRequested();
		Controller.ClearScrollback();
		Task<AgentProcessExit> completion;
		lock (_gate) {
			if (_completion is not null) {
				throw new InvalidOperationException("An ACP authentication terminal is already active.");
			}
			_launch = launch;
			_completion = new TaskCompletionSource<AgentProcessExit>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			completion = _completion.Task;
		}
		_agentMessages.Publish("authenticationTerminal", new { active = true });
		try {
			return await completion.WaitAsync(ct).ConfigureAwait(false);
		} finally {
			Controller.Stop();
			lock (_gate) {
				_launch = null;
				_completion = null;
			}
			_agentMessages.Publish("authenticationTerminal", new { active = false });
		}
	}

	public AgentLaunch ResolveLaunch() {
		lock (_gate) return _launch
			?? throw new InvalidOperationException("No ACP authentication launch is active.");
	}

	public void ObserveTerminalOutput(ReadOnlyMemory<byte> data) { }

	public void ObserveTerminalInput(ReadOnlyMemory<byte> data) { }

	public void ObserveProcessExit(AgentProcessExit exit) {
		lock (_gate) _completion?.TrySetResult(exit);
	}

	private void ObserveSupervisor(SupervisorStateChanged change) {
		if (change.State is not (SupervisorState.Idle or SupervisorState.Failed)
			|| change.ExitCode is not int exitCode) return;
		lock (_gate) {
			_completion?.TrySetResult(new AgentProcessExit { ExitCode = exitCode, Unexpected = true });
		}
	}

	public ValueTask DisposeAsync() {
		Controller.SupervisorChanged -= ObserveSupervisor;
		Controller.Dispose();
		lock (_gate) _completion?.TrySetCanceled();
		return ValueTask.CompletedTask;
	}
}
