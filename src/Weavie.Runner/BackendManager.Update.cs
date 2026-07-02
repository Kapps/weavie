using Weavie.Core.Processes;

namespace Weavie.Runner;

// The drain-and-swap half of BackendManager: applies a staged update to the running worker while
// preserving the WorkspaceBackend (same port + token — reconnecting tabs and the TLS-front mapping
// depend on both), rolling back via the supervisor's crash-loop breaker. All lifecycle mutations run
// behind the same _gate Ensure() uses, so a concurrent /backend hit can't re-provision mid-swap.
// See docs/specs/runner-auto-update.md.
public sealed partial class BackendManager {
	private bool _updating;

	/// <summary>
	/// Applies the staged version to the worker: asks it to drain (it exits 0 at the first quiet moment —
	/// unbounded by design; only the user's restart-now accelerates it), respawns the same backend from the
	/// staged version, confirms the running build over <c>/control/status</c>, and rolls back to the
	/// confirmed-good version when the new one trips the crash-loop breaker. Progress and the terminal
	/// outcome go to <paramref name="report"/> as (phase, detail) — sticky outcomes are <c>rolled-back</c>
	/// and <c>failed</c>. No-op when an apply is already in flight (the staged build only got newer; the
	/// respawn resolves the newest).
	/// </summary>
	public async Task ApplyStagedUpdateAsync(VersionStore store, Action<string, string?> report, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(report);
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
				// No worker was ever provisioned; the next Ensure() spawns straight from the staged version.
				report("idle", null);
				return;
			}

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

				if (supervisor.State == SupervisorState.Running && !await TryDrainAsync(backend, report).ConfigureAwait(false)) {
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

	private async Task<bool> TryDrainAsync(WorkspaceBackend backend, Action<string, string?> report) {
		try {
			using var response = await _http.PostAsync(ControlUrl(backend, "drain"), content: null).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode) {
				report("updating", $"worker refused the drain request ({(int)response.StatusCode})");
				return false;
			}

			return true;
		} catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
			// TaskCanceledException = the HttpClient timeout: a hung worker is a retry, never a crash.
			report("updating", $"worker not answering the drain request yet: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Waits for the respawned worker to answer <c>/control/status</c> and confirms whatever build it is
	/// serving; a crash-loop (<see cref="SupervisorState.Failed"/>) instead rolls back to the confirmed-good
	/// version and respawns. A rollback stays the reported outcome even after the restored build comes up;
	/// a rollback that itself trips the breaker is reported <c>failed</c> and left stopped — the picker page
	/// shows it, and Ensure() re-provisions on the next visit.
	/// </summary>
	private async Task ConfirmOrRollbackAsync(WorkspaceBackend backend, VersionStore store, Action<string, string?> report, CancellationToken ct) {
		int? rolledBackFrom = null;
		var supervisor = backend.Supervisor!;
		while (true) {
			ct.ThrowIfCancellationRequested();
			if (supervisor.State == SupervisorState.Failed) {
				if (rolledBackFrom is not null) {
					report("failed", "rollback build also failed to start — worker left stopped; see the runner console");
					return;
				}

				int? badBuild = store.StagedBuild;
				int? restored = store.RollbackToConfirmed();
				if (restored is null) {
					report("failed", "new build crash-looped and no confirmed-good build exists to roll back to");
					return;
				}

				report("rolled-back", $"build {badBuild} crash-looped — rolled back to build {restored}");
				rolledBackFrom = badBuild;
				lock (_gate) {
					supervisor.Stop();
					supervisor.Start();
				}

				continue;
			}

			if (await TryReadBuildAsync(backend).ConfigureAwait(false) is { } running) {
				store.MarkConfirmedGood(running);
				// After a rollback the sticky rolled-back outcome stays; a clean update settles to idle.
				if (rolledBackFrom is null) {
					report("idle", null);
				}

				return;
			}

			await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
		}
	}

	private async Task<int?> TryReadBuildAsync(WorkspaceBackend backend) {
		try {
			using var response = await _http.GetAsync(ControlUrl(backend, "status")).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode) {
				return null;
			}

			using var status = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
			string? buildNumber = status.RootElement.GetProperty("buildNumber").GetString();
			return buildNumber is null ? null : RunnerIdentity.ParseBuild(buildNumber);
		} catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or FormatException) {
			return null;
		}
	}

	// The worker's loopback control endpoint; its auth is the worker token as a query parameter.
	private Uri ControlUrl(WorkspaceBackend backend, string action) {
		string host = _workerHost.Contains(':', StringComparison.Ordinal) ? $"[{_workerHost}]" : _workerHost;
		return new Uri($"http://{host}:{backend.Port}/control/{action}?token={backend.Token}");
	}
}
