using System.Net;
using System.Text.Json;
using Weavie.Core.Processes;

namespace Weavie.Runner;

public sealed partial class BackendManager {
	private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan HealthRequestDeadline = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan StartupDeadline = TimeSpan.FromMinutes(2);
	private const int UnreachableFailureLimit = 3;

	private readonly CancellationTokenSource _healthCancellation = new();
	private readonly Task _healthMonitor;

	private async Task MonitorHealthAsync() {
		var state = new WorkerHealthMonitorState();
		try {
			while (await DelayHealthPollAsync(_healthCancellation.Token).ConfigureAwait(false)) {
				try {
					WorkspaceBackend? backend;
					bool updating;
					lock (_gate) {
						backend = _backend;
						updating = _updating;
					}

					var supervisor = backend?.Supervisor;
					if (backend is null || supervisor is null || updating || supervisor.State != SupervisorState.Running) {
						state.Clear();
						continue;
					}

					state.Observe(backend, supervisor, DateTimeOffset.UtcNow);

					if (!state.Ready) {
						using var startupProbe = HealthDeadline(_healthCancellation.Token);
						try {
							state.Ready = await TryReadBuildAsync(backend, startupProbe.Token).ConfigureAwait(false) is not null;
						} catch (OperationCanceledException) when (!_healthCancellation.IsCancellationRequested) {
							state.Ready = false;
						}
						if (!state.Ready && DateTimeOffset.UtcNow - state.GenerationStarted >= StartupDeadline) {
							ReplaceUnhealthy(backend, state.Generation, $"worker startup did not complete within {StartupDeadline.TotalSeconds:F0} seconds");
						}

						continue;
					}

					var health = await ProbeHealthAsync(backend, _healthCancellation.Token).ConfigureAwait(false);
					switch (health.State) {
						case WorkerHealthState.Healthy:
							state.UnreachableFailures = 0;
							break;
						case WorkerHealthState.Unhealthy:
							ReplaceUnhealthy(backend, state.Generation, health.Detail);
							break;
						case WorkerHealthState.Unreachable:
							state.UnreachableFailures++;
							if (state.UnreachableFailures >= UnreachableFailureLimit) {
								ReplaceUnhealthy(
									backend,
									state.Generation,
									$"worker health probe failed {state.UnreachableFailures} times: {health.Detail}");
							}

							break;
					}
				} catch (Exception ex) when (!_healthCancellation.IsCancellationRequested) {
					Console.WriteLine($"[health] monitor iteration failed: {ex}");
					Console.Out.Flush();
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
				return new WorkerHealth(WorkerHealthState.Healthy, "healthy");
			}

			if (response.StatusCode == HttpStatusCode.ServiceUnavailable) {
				return new WorkerHealth(WorkerHealthState.Unhealthy, HealthFailureDetail(body));
			}

			return new WorkerHealth(
				WorkerHealthState.Unreachable,
				$"health endpoint returned HTTP {(int)response.StatusCode}");
		} catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
			return new WorkerHealth(
				WorkerHealthState.Unreachable,
				$"health endpoint did not answer within {HealthRequestDeadline.TotalSeconds:F0} seconds");
		} catch (HttpRequestException ex) {
			return new WorkerHealth(WorkerHealthState.Unreachable, ex.Message);
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
			if (_updating
				|| !ReferenceEquals(_backend, backend)
				|| backend.Supervisor is not { State: SupervisorState.Running } current
				|| current.Generation != generation) {
				return;
			}

			supervisor = current;
		}

		Console.WriteLine($"[health] replacing backend generation {generation}: {reason}");
		Console.Out.Flush();
		supervisor.ReportUnhealthy(generation, reason);
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
	Unhealthy,
	Unreachable,
}

internal sealed record WorkerHealth(WorkerHealthState State, string Detail);

internal sealed class WorkerHealthMonitorState {
	private WorkspaceBackend? _backend;
	private ProcessSupervisor? _supervisor;

	public long Generation { get; private set; }

	public DateTimeOffset GenerationStarted { get; private set; }

	public bool Ready { get; set; }

	public int UnreachableFailures { get; set; }

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
		UnreachableFailures = 0;
	}

	public void Clear() {
		_backend = null;
		_supervisor = null;
		Generation = 0;
		GenerationStarted = default;
		Ready = false;
		UnreachableFailures = 0;
	}
}
