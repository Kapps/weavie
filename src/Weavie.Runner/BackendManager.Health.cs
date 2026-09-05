using System.Net;
using System.Text.Json;
using Weavie.Core.Diagnostics;
using Weavie.Core.Processes;

namespace Weavie.Runner;

public sealed partial class BackendManager {
	private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan HealthRequestDeadline = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan StartupDeadline = TimeSpan.FromMinutes(2);

	// A worker mid-request (e.g. standing up a second session's PTY + IDE-MCP server) can starve the thread
	// pool for one health-poll interval on a loaded CI runner without actually being stuck — its own message
	// operations carry a much longer (60s default) deadline and report themselves as merely "busy". Requiring
	// two consecutive misses before replacing the backend absorbs that one-off scheduling delay while still
	// catching a genuinely wedged worker on the very next poll, 5s later.
	private const int UnhealthyConfirmationThreshold = 2;

	private readonly CancellationTokenSource _healthCancellation = new();
	private readonly DiagnosticWorker _healthDiagnostics = new(message => {
		Console.WriteLine($"[health] {message}");
		Console.Out.Flush();
	});
	private readonly Task _healthMonitor;

	private async Task MonitorHealthAsync() {
		var state = new WorkerHealthMonitorState();
		try {
			while (await DelayHealthPollAsync(_healthCancellation.Token).ConfigureAwait(false)) {
				try {
					WorkspaceBackend? backend;
					lock (_gate) {
						backend = _backend;
					}

					var supervisor = backend?.Supervisor;
					if (backend is null || supervisor is null || supervisor.State != SupervisorState.Running) {
						state.Clear();
						continue;
					}

					state.Observe(backend, supervisor, DateTimeOffset.UtcNow);

					if (!state.Ready) {
						var statusProbe = await ProbeStatusAsync(backend, _healthCancellation.Token).ConfigureAwait(false);
						if (!IsCurrentGeneration(backend, supervisor, state.Generation)) {
							continue;
						}

						if (statusProbe.State == WorkerStatusProbeState.ProtocolFailure) {
							ReplaceUnhealthy(backend, state.Generation, statusProbe.Failure!);
							continue;
						}

						if (statusProbe.Status is { } status) {
							if (WorkerContractFailure(status) is { } failure) {
								ReplaceUnhealthy(backend, state.Generation, failure);
								continue;
							}

							state.MarkReady();
						}
						if (!state.Ready && DateTimeOffset.UtcNow - state.GenerationStarted >= StartupDeadline) {
							ReplaceUnhealthy(backend, state.Generation, $"worker startup did not complete within {StartupDeadline.TotalSeconds:F0} seconds");
						}

						continue;
					}
					var health = await ProbeHealthAsync(backend, _healthCancellation.Token).ConfigureAwait(false);
					switch (health.State) {
						case WorkerHealthState.Healthy:
							state.ClearUnhealthyStreak();
							supervisor.ReportHealthy(state.Generation);
							break;
						case WorkerHealthState.Busy:
							state.ClearUnhealthyStreak();
							break;
						case WorkerHealthState.Unhealthy:
							if (state.ObserveUnhealthy() >= UnhealthyConfirmationThreshold) {
								ReplaceUnhealthy(backend, state.Generation, health.Detail);
							}
							break;
					}
				} catch (Exception ex) when (!_healthCancellation.IsCancellationRequested) {
					LogHealth($"monitor iteration failed: {ex}");
				}
			}
		} catch (OperationCanceledException) when (_healthCancellation.IsCancellationRequested) {
		}
	}

	internal async Task<WorkerHealth> ProbeHealthAsync(WorkspaceBackend backend, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(backend);
		using var deadline = HealthDeadline(ct);
		try {
			using var response = await _http.GetAsync(ControlUrl(backend, "health"), deadline.Token).ConfigureAwait(false);
			string body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
			if (response.IsSuccessStatusCode) {
				return SuccessfulHealth(body);
			}

			if (response.StatusCode == HttpStatusCode.ServiceUnavailable) {
				return new WorkerHealth(WorkerHealthState.Unhealthy, HealthFailureDetail(body));
			}

			return new WorkerHealth(
				WorkerHealthState.Unhealthy,
				$"health endpoint returned HTTP {(int)response.StatusCode}");
		} catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
			return new WorkerHealth(
				WorkerHealthState.Unhealthy,
				$"health endpoint did not answer within {HealthRequestDeadline.TotalSeconds:F0} seconds");
		} catch (HttpRequestException ex) {
			return new WorkerHealth(WorkerHealthState.Unhealthy, ex.Message);
		}
	}

	private static async Task<bool> DelayHealthPollAsync(CancellationToken ct) {
		await Task.Delay(HealthPollInterval, ct).ConfigureAwait(false);
		return true;
	}

	private static CancellationTokenSource HealthDeadline(CancellationToken ct) {
		var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(HealthRequestDeadline);
		return deadline;
	}

	private void ReplaceUnhealthy(WorkspaceBackend backend, long generation, string reason) {
		ProcessSupervisor supervisor;
		lock (_gate) {
			if (!ReferenceEquals(_backend, backend)
				|| backend.Supervisor is not { State: SupervisorState.Running } current
				|| current.Generation != generation) {
				return;
			}

			supervisor = current;
		}

		if (supervisor.ReportUnhealthy(generation, reason)) {
			LogHealth($"replacing backend generation {generation}: {reason}");
		}
	}

	private bool IsCurrentGeneration(
		WorkspaceBackend backend,
		ProcessSupervisor supervisor,
		long generation) {
		lock (_gate) {
			return (_backend is null || ReferenceEquals(_backend, backend))
				&& ReferenceEquals(backend.Supervisor, supervisor)
				&& supervisor.State == SupervisorState.Running
				&& supervisor.Generation == generation;
		}
	}

	private void LogHealth(string message) => _healthDiagnostics.Report(message);

	private static WorkerHealth SuccessfulHealth(string body) {
		try {
			using var document = JsonDocument.Parse(body);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object
				|| !root.TryGetProperty("healthy", out var healthy)
				|| healthy.ValueKind != JsonValueKind.True
				|| !root.TryGetProperty("activeOperations", out var active)
				|| active.ValueKind != JsonValueKind.Array) {
				return new WorkerHealth(WorkerHealthState.Unhealthy, "worker returned a malformed health payload");
			}

			if (active.GetArrayLength() == 0) {
				return new WorkerHealth(WorkerHealthState.Healthy, "healthy");
			}

			return active[0].ValueKind == JsonValueKind.Object
				? new WorkerHealth(WorkerHealthState.Busy,
					$"worker has an active message operation: {OperationDetail(active[0])}")
				: new WorkerHealth(WorkerHealthState.Unhealthy, "worker returned a malformed health payload");
		} catch (JsonException) {
			return new WorkerHealth(WorkerHealthState.Unhealthy, "worker returned a malformed health payload");
		}
	}

	private static string HealthFailureDetail(string body) {
		try {
			using var document = JsonDocument.Parse(body);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) {
				return "worker reported unhealthy with a malformed health payload";
			}

			if (root.TryGetProperty("lastFailure", out var failure)
				&& failure.ValueKind == JsonValueKind.Object) {
				return OperationDetail(failure);
			}

			if (root.TryGetProperty("ingressResponsive", out var ingress)
				&& ingress.ValueKind == JsonValueKind.False) {
				if (root.TryGetProperty("activeOperations", out var active)
					&& active.ValueKind == JsonValueKind.Array
					&& active.GetArrayLength() > 0
					&& active[0].ValueKind == JsonValueKind.Object) {
					return $"worker message ingress is unresponsive; oldest active operation {OperationDetail(active[0])}";
				}

				return "worker message ingress is unresponsive with no active message operation";
			}

			return "worker reported unhealthy without a message-operation failure";
		} catch (JsonException) {
			return "worker reported unhealthy with a malformed health payload";
		}
	}

	private static string OperationDetail(JsonElement operation) =>
		$"{Read(operation, "id")} {Read(operation, "endpoint")} "
		+ $"{Read(operation, "feature")}.{Read(operation, "name")} "
		+ $"stuck in {Read(operation, "stage")} for {ReadNumber(operation, "elapsedMs")} ms";

	private static string Read(JsonElement element, string property) =>
		element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? "?"
			: "?";

	private static long ReadNumber(JsonElement element, string property) =>
		element.TryGetProperty(property, out var value) && value.TryGetInt64(out long number) ? number : 0;
}

internal enum WorkerHealthState {
	Healthy,
	Busy,
	Unhealthy,
}

internal sealed record WorkerHealth(WorkerHealthState State, string Detail);

internal sealed class WorkerHealthMonitorState {
	private WorkspaceBackend? _backend;
	private ProcessSupervisor? _supervisor;
	private int _unhealthyStreak;

	public long Generation { get; private set; }

	public DateTimeOffset GenerationStarted { get; private set; }

	public bool Ready { get; private set; }

	public void Observe(WorkspaceBackend backend, ProcessSupervisor supervisor, DateTimeOffset now) {
		if (ReferenceEquals(_backend, backend)
			&& ReferenceEquals(_supervisor, supervisor)
			&& Generation == supervisor.Generation) {
			return;
		}

		_backend = backend;
		_supervisor = supervisor;
		Generation = supervisor.Generation;
		GenerationStarted = now;
		Ready = false;
		_unhealthyStreak = 0;
	}

	public void MarkReady() => Ready = true;

	/// <summary>Records one unhealthy probe against this generation and returns the consecutive-miss count.</summary>
	public int ObserveUnhealthy() => ++_unhealthyStreak;

	/// <summary>Clears the consecutive-miss count — a healthy or merely-busy probe means the worker did answer.</summary>
	public void ClearUnhealthyStreak() => _unhealthyStreak = 0;

	public void Clear() {
		_backend = null;
		_supervisor = null;
		Generation = 0;
		GenerationStarted = default;
		Ready = false;
		_unhealthyStreak = 0;
	}
}
