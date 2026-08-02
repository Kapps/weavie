using System.Text.Json;
using Weavie.Core.Processes;

namespace Weavie.Runner;

// The confirm/swap half of BackendManager: brings the worker onto the staged version and confirms it,
// preserving the WorkspaceBackend (same port + token — reconnecting tabs and the TLS-front mapping
// depend on both), rolling back via the supervisor's crash-loop breaker. A runtime update drains + swaps
// a running old worker; boot recovery reaches this path only when the staged bundle has the live runner's
// exact contract. All lifecycle mutations run behind the same _gate Ensure() uses, so a concurrent /backend
// hit can't re-provision mid-swap. See docs/specs/runner-auto-update.md.
public sealed partial class BackendManager {
	private bool _updating;

	/// <summary>
	/// Applies a newly-staged version to the RUNNING worker: asks it to drain (it exits 0 at the first quiet
	/// moment — unbounded by design; only the user's restart-now accelerates it), respawns the same backend
	/// from the staged version, confirms its exact identity and health probation, and rolls back to the
	/// confirmed-good version when the new one trips the breaker. Progress and the terminal outcome go to
	/// <paramref name="report"/> as (phase, detail). No-op when an apply is already in flight (the staged build
	/// only got newer; the respawn resolves the newest).
	/// </summary>
	public Task ApplyStagedUpdateAsync(VersionStore store, Action<string, string?> report, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(report);
		return RunExclusiveAsync(report, async backend => {
			report("updating", "waiting for the workspace to go quiet");
			await DrainUntilStoppedAsync(backend, report, ct).ConfigureAwait(false);

			report("updating", "restarting the worker");
			lock (_gate) {
				// Stop() (not just a respawn) so the swap starts with a clean crash history — a rollback
				// restarted straight from Failed would inherit the bad build's crashes and insta-trip the breaker.
				backend.Supervisor!.Stop();
				backend.Supervisor.Start();
			}

			await ConfirmOrRollbackAsync(backend, store, report, ct).ConfigureAwait(false);
		});
	}

	/// <summary>
	/// Boot recovery for an exact-contract staged ≠ confirmed store: confirms (or rolls back) the worker
	/// Ensure() already spawned straight from the staged version. It does not drain or restart because there is
	/// no old build to swap away from. Reports and rolls back exactly like <see cref="ApplyStagedUpdateAsync"/>.
	/// </summary>
	public Task ConfirmStagedWorkerAsync(VersionStore store, Action<string, string?> report, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(report);
		return RunExclusiveAsync(report, backend => ConfirmOrRollbackAsync(backend, store, report, ct));
	}

	// Serializes an update action behind the _updating guard (a concurrent Ensure() hands the backend back
	// as-is while it runs). A null backend means none was ever provisioned — the next Ensure() spawns
	// straight from the staged version, so there is nothing to confirm.
	private async Task RunExclusiveAsync(Action<string, string?> report, Func<WorkspaceBackend, Task> body) {
		WorkspaceBackend? backend;
		lock (_gate) {
			if (_updating) {
				return;
			}

			_updating = true;
			backend = _backend;
		}

		try {
			if (backend is null) {
				report("idle", null);
				return;
			}

			await body(backend).ConfigureAwait(false);
		} finally {
			lock (_gate) {
				_updating = false;
			}
		}
	}

	/// <summary>
	/// Requests drain and waits for the worker to stop. A worker that crashes mid-drain is relaunched by its
	/// supervisor with no memory of the drain, so every return to Running re-requests it.
	/// </summary>
	private async Task DrainUntilStoppedAsync(WorkspaceBackend backend, Action<string, string?> report, CancellationToken ct) {
		var supervisor = backend.Supervisor!;
		// Released on every supervisor transition; a single subscription for the whole wait (an abandoned
		// per-wait handler would otherwise pile up while the worker is up but unresponsive).
		var settled = new SemaphoreSlim(0);
		void OnChange(SupervisorStateChanged change) => settled.Release();
		supervisor.StateChanged += OnChange;
		try {
			while (true) {
				ct.ThrowIfCancellationRequested();
				if (supervisor.State is SupervisorState.Idle or SupervisorState.Failed) {
					return;
				}

				if (supervisor.State == SupervisorState.Running && !await TryDrainAsync(backend, report, ct).ConfigureAwait(false)) {
					// The worker is up but not answering yet (it may have just respawned); retry shortly.
					await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
					continue;
				}

				await settled.WaitAsync(ct).ConfigureAwait(false);
			}
		} finally {
			supervisor.StateChanged -= OnChange;
		}
	}

	internal async Task<bool> TryDrainAsync(
		WorkspaceBackend backend,
		Action<string, string?> report,
		CancellationToken ct) {
		using var deadline = HealthDeadline(ct);
		try {
			using var response = await _http.PostAsync(
				ControlUrl(backend, "drain"),
				content: null,
				deadline.Token).ConfigureAwait(false);
			if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable) {
				report("updating", "worker is still starting; drain will retry");
				return false;
			}

			if (!response.IsSuccessStatusCode) {
				report("updating", $"worker refused the drain request ({(int)response.StatusCode})");
				return false;
			}

			report("updating", "waiting for the workspace to go quiet");
			return true;
		} catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException) {
			report("updating", $"worker not answering the drain request yet: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Confirms the exact staged identity only after mandatory health probation on one generation. A failed
	/// generation is supervised through the breaker, then rolls back live when the confirmed bundle has the
	/// same contract. A protocol mismatch is terminal until the matching bundle is installed/restarted, and a
	/// rollback candidate that also fails is left stopped.
	/// </summary>
	private async Task ConfirmOrRollbackAsync(WorkspaceBackend backend, VersionStore store, Action<string, string?> report, CancellationToken ct) {
		int? rolledBackFrom = null;
		var supervisor = backend.Supervisor!;
		while (true) {
			ct.ThrowIfCancellationRequested();
			if (supervisor.State is SupervisorState.Failed or SupervisorState.Idle) {
				if (rolledBackFrom is not null) {
					report("failed", "rollback build also failed to start — worker left stopped; see the runner console");
					return;
				}

				int? badBuild = store.StagedBuild;
				var (restoredBuild, rollbackFailure) = store.RollbackToConfirmed(RunnerIdentity.SpawnContract);
				if (restoredBuild is not { } restored) {
					report("failed", $"build {badBuild} failed health probation and rollback was refused: {rollbackFailure}");
					return;
				}

				report("rolled-back", $"build {badBuild} failed health probation — rolled back to build {restored}");
				rolledBackFrom = badBuild;
				lock (_gate) {
					supervisor.Stop();
					supervisor.Start();
				}

				continue;
			}

			if (supervisor.State != SupervisorState.Running) {
				await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
				continue;
			}

			long generation = supervisor.Generation;
			var statusProbe = await ProbeStatusAsync(backend, ct).ConfigureAwait(false);
			if (!IsCurrentGeneration(backend, supervisor, generation)) {
				continue;
			}

			if (statusProbe.State == WorkerStatusProbeState.Pending) {
				await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
				continue;
			}

			if (statusProbe.State == WorkerStatusProbeState.ProtocolFailure) {
				ReplaceUnhealthy(backend, generation, statusProbe.Failure!);
				continue;
			}

			int expectedBuild = store.StagedBuild
				?? throw new InvalidOperationException("cannot confirm a worker when no build is staged");
			if (WorkerIdentityFailure(statusProbe.Status!, expectedBuild) is { } failure) {
				ReplaceUnhealthy(backend, generation, failure);
				continue;
			}

			var health = await ProbeHealthAsync(backend, ct).ConfigureAwait(false);
			if (!IsCurrentGeneration(backend, supervisor, generation)) {
				continue;
			}

			if (health.State == WorkerHealthState.Busy) {
				await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
				continue;
			}

			if (health.State != WorkerHealthState.Healthy) {
				ReplaceUnhealthy(backend, generation, $"worker failed update health probation: {health.Detail}");
				continue;
			}

			if (!supervisor.ReportHealthy(generation)) {
				await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
				continue;
			}

			store.MarkConfirmedGood(expectedBuild);

			// After a rollback the sticky rolled-back outcome stays; a clean update settles to idle.
			if (rolledBackFrom is null) {
				report("idle", null);
			}

			return;
		}
	}

	internal async Task<WorkerStatusProbe> ProbeStatusAsync(WorkspaceBackend backend, CancellationToken ct) {
		using var deadline = HealthDeadline(ct);
		try {
			using var response = await _http.GetAsync(ControlUrl(backend, "status"), deadline.Token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode) {
				return response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
					? WorkerStatusProbe.Pending()
					: WorkerStatusProbe.ProtocolFailure(
						$"worker status endpoint returned HTTP {(int)response.StatusCode}");
			}

			try {
				using var status = JsonDocument.Parse(
					await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
				var root = status.RootElement;
				if (root.ValueKind != JsonValueKind.Object
					|| !root.TryGetProperty("buildNumber", out var buildNumberValue)
					|| buildNumberValue.ValueKind != JsonValueKind.String
					|| buildNumberValue.GetString() is not { Length: > 0 } buildNumber
					|| !root.TryGetProperty("spawnContract", out var spawnContractValue)
					|| spawnContractValue.ValueKind != JsonValueKind.Number
					|| !spawnContractValue.TryGetInt32(out int spawnContract)) {
					return WorkerStatusProbe.ProtocolFailure(
						"worker returned malformed status identity; buildNumber and spawnContract are required");
				}

				return WorkerStatusProbe.Ready(
					new WorkerControlStatus(RunnerIdentity.ParseBuild(buildNumber), spawnContract));
			} catch (Exception ex) when (ex is JsonException or FormatException) {
				return WorkerStatusProbe.ProtocolFailure($"worker returned malformed status identity: {ex.Message}");
			}
		} catch (Exception ex) when (!ct.IsCancellationRequested
			&& ex is HttpRequestException or OperationCanceledException) {
			return WorkerStatusProbe.Pending();
		}
	}

	internal static string? WorkerContractFailure(WorkerControlStatus status) =>
		status.SpawnContract == RunnerIdentity.SpawnContract
			? null
			: $"worker spawn contract {status.SpawnContract} does not match runner contract "
				+ RunnerIdentity.SpawnContract;

	internal static string? WorkerIdentityFailure(WorkerControlStatus status, int expectedBuild) =>
		WorkerContractFailure(status)
		?? (status.Build == expectedBuild
			? null
			: $"worker reported build {status.Build}, but staged build {expectedBuild} was expected");

	// The worker's loopback control endpoint; its auth is the worker token as a query parameter.
	private Uri ControlUrl(WorkspaceBackend backend, string action) {
		string host = _workerHost.Contains(':', StringComparison.Ordinal) ? $"[{_workerHost}]" : _workerHost;
		return new Uri($"http://{host}:{backend.Port}/control/{action}?token={backend.Token}");
	}
}

internal sealed record WorkerControlStatus(int Build, int SpawnContract);

internal enum WorkerStatusProbeState {
	Pending,
	Ready,
	ProtocolFailure,
}

internal sealed record WorkerStatusProbe(
	WorkerStatusProbeState State,
	WorkerControlStatus? Status,
	string? Failure) {
	public static WorkerStatusProbe Pending() => new(WorkerStatusProbeState.Pending, null, null);

	public static WorkerStatusProbe Ready(WorkerControlStatus status) =>
		new(WorkerStatusProbeState.Ready, status, null);

	public static WorkerStatusProbe ProtocolFailure(string failure) =>
		new(WorkerStatusProbeState.ProtocolFailure, null, failure);
}
