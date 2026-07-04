using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Weavie.Core;

namespace Weavie.Runner;

/// <summary>A point-in-time view of the updater for the picker page (built even when updates are off).</summary>
public sealed record UpdateStatus {
	/// <summary>Whether <c>--auto-update</c> is on.</summary>
	public required bool Enabled { get; init; }

	/// <summary>The runner's own build identity.</summary>
	public required string RunnerBuild { get; init; }

	/// <summary>The staged (current-symlink) build, when a managed version exists.</summary>
	public int? Staged { get; init; }

	/// <summary>The last build confirmed serving.</summary>
	public int? Confirmed { get; init; }

	/// <summary>What the updater is doing: <c>idle</c>, <c>updating</c>, <c>needs-newer-runner</c>, or <c>error</c>.</summary>
	public required string Phase { get; init; }

	/// <summary>Human detail for the phase (the hold, the error, the rollback), when there is one.</summary>
	public string? Detail { get; init; }

	/// <summary>True when the runner executes an older version dir than <c>current</c> — a restart applies it.</summary>
	public bool RunnerBehind { get; init; }
}

/// <summary>
/// Polls the rolling <c>main-latest</c> prerelease for a newer runner bundle, stages it into the
/// <see cref="VersionStore"/> (digest-verified, spawn-contract-checked), and asks the
/// <see cref="BackendManager"/> to drain-and-swap the worker. The runner itself keeps executing its
/// version; only a restart picks the staged one up. See docs/specs/runner-auto-update.md.
/// </summary>
public sealed class UpdatePoller : IDisposable {
	private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
	private const string ReleaseTag = "main-latest";
	private const string ReleaseApi = "https://api.github.com/repos/Kapps/weavie/releases/tags/" + ReleaseTag;
	private const string AssetName = "weavie-runner-linux-x64.tar.gz";

	private readonly VersionStore _store;
	private readonly BackendManager _backends;
	private readonly HttpClient _http;
	private readonly Action<string> _log;
	private readonly CancellationTokenSource _stop = new();
	private readonly object _statusGate = new();
	// Digests this process decided not to stage, with the phase each decision keeps showing (a bundle
	// needing a newer runner must stay visible, not fade to "idle" on the next poll) — skipped without
	// re-downloading. In-memory only: a restarted (= upgraded) runner reconsiders them.
	private readonly Dictionary<string, (string Phase, string? Detail)> _skipped = [];
	private string _phase = "idle";
	private string? _detail;

	/// <summary>Creates a poller over <paramref name="store"/>, updating <paramref name="backends"/>'s worker.</summary>
	public UpdatePoller(RunnerOptions options, VersionStore store, BackendManager backends, Action<string> log) {
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(backends);
		ArgumentNullException.ThrowIfNull(log);
		_store = store;
		_backends = backends;
		_log = log;
		_http = new HttpClient();
		_http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("weavie-runner", RunnerIdentity.BuildNumber));
		_http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		if (!string.IsNullOrEmpty(options.GitHubToken)) {
			_http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.GitHubToken);
		}
	}

	/// <summary>Starts the update loop: reconcile a boot mid-update, then poll now and every 15 minutes.</summary>
	public void Start() => _ = Task.Run(async () => {
		_log($"enabled — runner build {RunnerIdentity.BuildNumber}, polling {ReleaseTag} every {PollInterval.TotalMinutes:0}m");
		// Staged ≠ confirmed at boot means a prior runner died mid-update: the worker Ensure() spawns is
		// an unconfirmed build, so run the apply flow first — its confirm/rollback watches that spawn.
		if (_store.StagedBuild is { } staged && staged != _store.ConfirmedGoodBuild) {
			await GuardedAsync(() => _backends.ApplyStagedUpdateAsync(_store, SetPhase, _stop.Token)).ConfigureAwait(false);
		}

		while (!_stop.IsCancellationRequested) {
			await GuardedAsync(() => PollOnceAsync(_stop.Token)).ConfigureAwait(false);
			try {
				await Task.Delay(PollInterval, _stop.Token).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				return;
			}
		}
	});

	// One guarded step of the loop: only disposal ends it. An HttpClient timeout throws
	// TaskCanceledException (an OperationCanceledException), so a slow download or hung worker must
	// surface as a failed step and retry — never silently kill updates for the life of the process.
	private async Task GuardedAsync(Func<Task> step) {
		try {
			await step().ConfigureAwait(false);
		} catch (Exception ex) when (!_stop.IsCancellationRequested
			&& ex is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException) {
			SetPhase("error", ex.Message);
			_log($"update step failed: {ex.Message}");
		} catch (OperationCanceledException) {
			// disposal
		}
	}

	/// <summary>The updater's current state for the picker page.</summary>
	public UpdateStatus Snapshot() {
		lock (_statusGate) {
			return new UpdateStatus {
				Enabled = true,
				RunnerBuild = RunnerIdentity.BuildNumber,
				Staged = _store.StagedBuild,
				Confirmed = _store.ConfirmedGoodBuild,
				Phase = _phase,
				Detail = _detail,
				RunnerBehind = RunnerIsBehind(),
			};
		}
	}

	/// <summary>The status shown when <c>--auto-update</c> is off: identity only, nothing polling.</summary>
	public static UpdateStatus Disabled() => new() {
		Enabled = false,
		RunnerBuild = RunnerIdentity.BuildNumber,
		Phase = "off",
	};

	private async Task PollOnceAsync(CancellationToken ct) {
		using var response = await _http.GetAsync(ReleaseApi, ct).ConfigureAwait(false);
		if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {
			// No main-latest release published yet — nothing to update from; the next poll re-checks.
			SetPhase("idle", "no main-latest release published yet");
			return;
		}

		response.EnsureSuccessStatusCode();
		using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
		var asset = FindAsset(release.RootElement);
		if (asset is not { } found) {
			SetPhase("error", $"main-latest has no {AssetName} asset");
			return;
		}

		lock (_statusGate) {
			// A previously-skipped bundle keeps showing why (a needs-newer-runner block must not fade to idle).
			if (_skipped.TryGetValue(found.Digest, out var kept)) {
				(_phase, _detail) = kept;
				return;
			}
		}

		if (_store.IsKnownDigest(found.Digest)) {
			// Nothing new; only transient phases reset — a sticky rolled-back / failed outcome stays visible.
			ClearTransientPhase();
			return;
		}

		SetPhase("updating", "downloading");
		_log($"downloading update bundle ({found.Digest})");
		// Staged on the store's own volume: Stage() moves the dir into versions/, and a move out of the
		// OS temp dir (often tmpfs) would fail cross-filesystem.
		string scratch = _store.CreateStagingDir();
		try {
			string tarball = Path.Combine(scratch, AssetName);
			await DownloadAsync(found.Url, tarball, ct).ConfigureAwait(false);
			VerifyDigest(tarball, found.Digest);

			var (manifest, versionDir) = VersionStore.ExtractBundle(tarball, Path.Combine(scratch, "x"));
			if (manifest.SpawnContract > RunnerIdentity.SpawnContract) {
				Skip(found.Digest, "needs-newer-runner",
					$"build {manifest.BuildNumber} needs spawn contract {manifest.SpawnContract} (runner speaks {RunnerIdentity.SpawnContract}) — restart the runner to continue updating");
				_log($"build {manifest.BuildNumber} requires a newer runner; not applied");
				return;
			}

			if (_store.StagedBuild is { } staged && manifest.BuildNumber <= staged) {
				Skip(found.Digest, "idle", null);
				return;
			}

			_store.Stage(manifest, versionDir, found.Digest);
		} finally {
			try {
				Directory.Delete(scratch, recursive: true);
			} catch (IOException) {
				// Best-effort; VersionStore clears leftover staging at the next open.
			}
		}

		// The apply flow reports its own outcome, including the sticky terminal ones (rolled-back, failed).
		await _backends.ApplyStagedUpdateAsync(_store, SetPhase, ct).ConfigureAwait(false);
	}

	private (string Url, string Digest)? FindAsset(JsonElement release) {
		if (!release.TryGetProperty("assets", out var assets)) {
			return null;
		}

		foreach (var asset in assets.EnumerateArray()) {
			if (asset.GetProperty("name").GetString() == AssetName) {
				string? url = asset.GetProperty("browser_download_url").GetString();
				string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
				if (url is null || digest is null) {
					return null;
				}

				return (url, digest);
			}
		}

		return null;
	}

	private async Task DownloadAsync(string url, string path, CancellationToken ct) {
		using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using var file = File.Create(path);
		await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
	}

	// The release API reports each asset's digest as "sha256:<hex>"; the download must match it exactly.
	private static void VerifyDigest(string path, string expected) {
		using var stream = File.OpenRead(path);
		string actual = "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
		if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) {
			throw new InvalidDataException($"bundle digest mismatch: expected {expected}, downloaded {actual}");
		}
	}

	// Records the decision not to stage this digest, and shows its phase now and on every later poll.
	private void Skip(string digest, string phase, string? detail) {
		lock (_statusGate) {
			_skipped[digest] = (phase, detail);
			_phase = phase;
			_detail = detail;
		}
	}

	private void SetPhase(string phase, string? detail) {
		lock (_statusGate) {
			_phase = phase;
			_detail = detail;
		}
	}

	// Ends an in-flight/transient phase when a poll finds nothing new; sticky outcomes (rolled-back,
	// failed, needs-newer-runner) stay until a genuinely new bundle supersedes them.
	private void ClearTransientPhase() {
		lock (_statusGate) {
			if (_phase is "updating" or "error") {
				_phase = "idle";
				_detail = null;
			}
		}
	}

	// Behind = the runner executes from a version dir that is no longer the staged one; a runner outside
	// any managed layout (a source build) is unmanaged, not behind.
	private bool RunnerIsBehind() =>
		ManagedRunnerLayout.RootContaining(AppContext.BaseDirectory) is not null
		&& _store.StagedBuild is { } staged
		&& RunnerIdentity.Build != staged;

	/// <inheritdoc/>
	public void Dispose() {
		_stop.Cancel();
		_http.Dispose();
	}
}
