using System.Diagnostics;
using System.Text.Json;
using Weavie.Core.Processes;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpJsonRpcConnection {
	private void StartProcess(SupervisedLaunch launch) {
		long generation = _supervisor.Generation;
		lock (_deliveryGate) {
			lock (_processGate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				_processGeneration = generation;
			}
		}
		Process? process = null;
		try {
			var definition = _currentDefinition()
				?? throw new InvalidOperationException("The ACP agent definition is unavailable.");
			if (!string.Equals(definition.Id, _providerId, StringComparison.Ordinal)) {
				throw new InvalidOperationException("An ACP agent definition cannot change provider identity.");
			}
			process = new Process {
				StartInfo = BuildStartInfo(definition),
				EnableRaisingEvents = false,
			};
			lock (_processGate) {
				if (_processGeneration != generation) {
					throw new InvalidOperationException("The ACP agent launch was stopped.");
				}
				_process = process;
			}
			if (!process.Start()) {
				throw new InvalidOperationException($"ACP agent '{definition.Name}' did not start.");
			}
		} catch (Exception ex) {
			var fault = new IOException($"ACP agent '{_providerName}' could not start.", ex);
			bool accepted = false;
			lock (_deliveryGate) {
				lock (_processGate) {
					if (_processGeneration == generation) {
						accepted = ClaimProtocolFaultSerialized(generation);
					}
				}
				lock (_processGate) {
					if (_processGeneration == generation) {
						_process = null;
						_processGeneration = 0;
					}
				}
			}
			if (accepted) PublishProtocolFault(generation, fault, reportUnhealthy: false);
			process?.Dispose();
			throw;
		}
		ProcessStarted?.Invoke(new AcpProcessGeneration(generation, launch.Attempt));
		var stdout = ReadStdoutAsync(process, launch, generation);
		var stderr = ReadStderrAsync(process);
		_ = ObserveProcessExitAsync(process, launch, generation, stdout, stderr);
	}

	private async Task ObserveProcessExitAsync(
		Process process,
		SupervisedLaunch launch,
		long generation,
		Task stdout,
		Task stderr) {
		int exitCode;
		try {
			await process.WaitForExitAsync().ConfigureAwait(false);
			exitCode = ReadExitCode(process);
			await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) {
			exitCode = -1;
		}
		bool ownsProcess;
		bool faulted = false;
		lock (_deliveryGate) {
			lock (_processGate) ownsProcess = ReferenceEquals(_process, process);
			if (ownsProcess && launch.IsCurrent) {
				faulted = ClaimProtocolFaultSerialized(generation);
			}
			lock (_processGate) {
				if (ReferenceEquals(_process, process)) {
					_process = null;
					_processGeneration = 0;
				}
			}
		}
		if (faulted) {
			PublishProtocolFault(
				generation,
				new IOException($"ACP agent exited with code {exitCode}."),
				reportUnhealthy: false);
		}
		launch.NotifyExited(exitCode);
		if (ownsProcess) process.Dispose();
	}

	private ProcessStartInfo BuildStartInfo(AcpAgentDefinition definition) {
		var invocation = AcpProcessInvocation.ResolveRedirectedProcess(definition, _workingDirectory, []);
		string command = invocation.Command;
		var arguments = invocation.Arguments;
		var info = new ProcessStartInfo(command) {
			WorkingDirectory = _workingDirectory,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = Utf8NoBom,
			StandardOutputEncoding = Utf8NoBom,
			StandardErrorEncoding = Utf8NoBom,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (string argument in arguments) {
			info.ArgumentList.Add(argument);
		}
		foreach (var entry in definition.Environment) {
			info.Environment[entry.Key] = entry.Value;
		}
		return info;
	}

	private async Task ReadStdoutAsync(Process process, SupervisedLaunch launch, long generation) {
		try {
			while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) {
				if (line.Length == 0) {
					continue;
				}
				try {
					Action? dispatch;
					lock (_deliveryGate) {
						lock (_processGate) if (_processGeneration != generation) return;
						dispatch = HandleLine(line, generation);
					}
					dispatch?.Invoke();
				} catch (Exception ex) {
					var fault = ex as AcpProtocolException
						?? new AcpProtocolException($"ACP agent output could not be handled: {ex.Message}", ex);
					SignalProtocolFault(generation, fault, reportUnhealthy: true);
					return;
				}
			}
			if (launch.IsCurrent && !process.HasExited) {
				var fault = new AcpProtocolException("ACP agent closed stdout while it was still running.");
				SignalProtocolFault(generation, fault, reportUnhealthy: true);
			}
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
			if (launch.IsCurrent) {
				SignalProtocolFault(
					generation,
					new AcpProtocolException($"ACP agent stdout failed: {ex.Message}", ex),
					reportUnhealthy: true);
			}
		}
	}

	private async Task ReadStderrAsync(Process process) {
		try {
			while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) {
				_log($"[acp:{_providerId}] {line}");
			}
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
			_log($"[acp:{_providerId}] stderr closed: {ex.Message}");
		}
	}

}
