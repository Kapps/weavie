using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Weavie.Core;

namespace Weavie.Runner;

/// <summary>A bundle's manifest.json, written by the Weavie.Runner publish (see the csproj).</summary>
public sealed record BundleManifest {
	/// <summary>The bundle's build number (the release workflow's run number; versions/ dir key).</summary>
	public required int BuildNumber { get; init; }

	/// <summary>The runner⟷worker spawn-contract generation this bundle requires of the running runner.</summary>
	public required int SpawnContract { get; init; }
}

/// <summary>
/// The managed on-disk version layout the auto-updater maintains (and a release tarball extracts to):
/// <c>versions/&lt;build&gt;/</c> bundles, a <c>current</c> symlink, and <c>state.json</c> recording the
/// staged / confirmed-good / bad builds. Workers are always spawned from a resolved version path, never
/// through the symlink (the worker serves wwwroot from its own base dir, so riding the symlink would swap
/// its web assets mid-flight). See docs/specs/runner-auto-update.md.
/// </summary>
public sealed class VersionStore {
	private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNameCaseInsensitive = true };
	private readonly object _gate = new();
	private readonly string _root;
	private readonly Action<string> _log;
	private StateFile _state;

	private VersionStore(string root, Action<string> log, StateFile state) {
		_root = root;
		_log = log;
		_state = state;
	}

	/// <summary>
	/// Opens the store at the managed layout containing the running runner (its base dir is
	/// <c>&lt;root&gt;/versions/&lt;build&gt;/</c>), or at <c>~/.weavie/runner</c> for a runner launched from
	/// anywhere else (a source build) — the first staged update then creates the layout there.
	/// </summary>
	public static VersionStore Open(Action<string> log) {
		ArgumentNullException.ThrowIfNull(log);
		string root = ManagedRunnerLayout.RootContaining(AppContext.BaseDirectory)
			?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weavie", "runner");
		return OpenAt(root, log);
	}

	/// <summary>Opens the store at an explicit root (the test seam; <see cref="Open"/> resolves the real one).</summary>
	internal static VersionStore OpenAt(string root, Action<string> log) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		ArgumentNullException.ThrowIfNull(log);
		root = Path.GetFullPath(root);
		Directory.CreateDirectory(Path.Combine(root, "versions"));
		// Crash leftovers from an interrupted download/extract; nothing live ever runs from staging.
		string staging = Path.Combine(root, "staging");
		if (Directory.Exists(staging)) {
			Directory.Delete(staging, recursive: true);
			log("cleared leftover staging dir");
		}

		var store = new VersionStore(root, log, StateFile.Load(Path.Combine(root, "state.json")));
		store.ReconcileStateFromCurrent();
		return store;
	}

	/// <summary>
	/// A fresh scratch dir for a download/extract, on the store's own volume — <see cref="Stage"/> moves the
	/// extracted dir into <c>versions/</c>, and a cross-filesystem move (OS temp is often tmpfs) would fail.
	/// </summary>
	public string CreateStagingDir() {
		string dir = Path.Combine(_root, "staging", Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	/// <summary>The build staged as <c>current</c>, or null before the first staging.</summary>
	public int? StagedBuild {
		get {
			lock (_gate) {
				return _state.Staged;
			}
		}
	}

	/// <summary>The last build confirmed serving, or null before the first confirm.</summary>
	public int? ConfirmedGoodBuild {
		get {
			lock (_gate) {
				return _state.ConfirmedGood;
			}
		}
	}

	/// <summary>Whether <paramref name="digest"/> is the staged bundle's or a known-bad one — nothing to download.</summary>
	public bool IsKnownDigest(string digest) {
		lock (_gate) {
			return digest == _state.StagedDigest || _state.BadDigests.Contains(digest);
		}
	}

	/// <summary>
	/// Associates a verified release digest with the installed staged build. A build number has one immutable
	/// identity, so publishing different content under the same number is rejected.
	/// </summary>
	public void RecordStagedDigest(int build, string digest) {
		ArgumentException.ThrowIfNullOrEmpty(digest);
		lock (_gate) {
			if (_state.Staged != build) {
				throw new InvalidOperationException($"build {build} is not the staged build {_state.Staged}");
			}

			if (_state.StagedDigest is { } existing && !string.Equals(existing, digest, StringComparison.OrdinalIgnoreCase)) {
				throw new InvalidDataException($"build {build} is already associated with a different release digest");
			}

			if (_state.StagedDigest is null) {
				_state = _state with { StagedDigest = digest };
				_state.Save();
			}
		}
	}

	/// <summary>
	/// The worker executable of the <c>current</c> version, resolved to its concrete <c>versions/&lt;build&gt;/</c>
	/// path, or null when no managed version exists (spawn falls back to the co-located worker probe).
	/// </summary>
	public string? ActiveWorkerPath() {
		lock (_gate) {
			if (_state.Staged is not { } build) {
				return null;
			}

			return WorkerExecutable(VersionDir(build));
		}
	}

	/// <summary>
	/// Adopts an extracted bundle as the staged version: installs its immutable version directory when absent,
	/// reuses a matching complete directory after an interrupted stage, re-points <c>current</c>, and records the
	/// verified <paramref name="digest"/>. The caller has already checked the digest and spawn contract.
	/// </summary>
	public void Stage(BundleManifest manifest, string extractedVersionDir, string digest) {
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentException.ThrowIfNullOrEmpty(extractedVersionDir);
		ArgumentException.ThrowIfNullOrEmpty(digest);
		lock (_gate) {
			string target = VersionDir(manifest.BuildNumber);
			if (Directory.Exists(target)) {
				var existing = ReadVersionManifest(target, manifest.BuildNumber);
				if (existing.SpawnContract != manifest.SpawnContract) {
					throw new InvalidDataException(
						$"version {manifest.BuildNumber} already exists with spawn contract {existing.SpawnContract}, not {manifest.SpawnContract}");
				}
			} else {
				Directory.Move(extractedVersionDir, target);
			}

			PointCurrentAt(manifest.BuildNumber);
			_state = _state with { Staged = manifest.BuildNumber, StagedDigest = digest };
			_state.Save();
			_log($"staged build {manifest.BuildNumber} -> {target}");
		}
	}

	/// <summary>
	/// Marks <paramref name="build"/> as confirmed serving, then prunes version dirs nothing can still be
	/// running from (keeps the confirmed build, the staged build, and the runner's own version). Each prune
	/// is logged with the freed path.
	/// </summary>
	public void MarkConfirmedGood(int build) {
		lock (_gate) {
			_state = _state with { ConfirmedGood = build };
			_state.Save();
			PruneLocked();
		}
	}

	/// <summary>
	/// Rolls back a bad staged build: records its digest as bad (never retried; a newer build supersedes it),
	/// re-points <c>current</c> at the confirmed-good build, and stages that. Returns the build rolled back
	/// to, or null when no confirmed-good build exists to roll back to (the failure is the caller's to surface).
	/// </summary>
	public int? RollbackToConfirmed() {
		lock (_gate) {
			if (_state.ConfirmedGood is not { } good || _state.Staged == good) {
				return null;
			}

			if (_state.StagedDigest is { } badDigest && !_state.BadDigests.Contains(badDigest)) {
				_state = _state with { BadDigests = [.. _state.BadDigests, badDigest] };
				_state.Save();
			}

			PointCurrentAt(good);
			_state = _state with { Staged = good, StagedDigest = null };
			_state.Save();
			_log($"rolled back to build {good}");
			return good;
		}
	}

	private string VersionDir(int build) => Path.Combine(_root, "versions", build.ToString());

	// The apphost executable when the bundle ships one (self-contained linux publish), else the dll
	// (a dev-staged bundle) — HeadlessLauncher runs either.
	private static string WorkerExecutable(string versionDir) {
		string apphost = Path.Combine(versionDir, "worker", "Weavie.Headless");
		return File.Exists(apphost) ? apphost : Path.Combine(versionDir, "worker", "Weavie.Headless.dll");
	}

	// Build the replacement link first, then rename it over current so a crash cannot leave current absent.
	private void PointCurrentAt(int build) {
		string current = Path.Combine(_root, "current");
		string replacement = current + ".new";
		File.Delete(replacement);
		Directory.CreateSymbolicLink(replacement, Path.Combine("versions", build.ToString()));
		try {
			File.Move(replacement, current, overwrite: true);
		} catch {
			File.Delete(replacement);
			throw;
		}
	}

	// The release tarball deliberately has no mutable state.json. Current is the crash-safe selector,
	// so it restores staged state both for a fresh install and for a crash between selector and state writes.
	private void ReconcileStateFromCurrent() {
		string current = Path.Combine(_root, "current");
		string? link = new FileInfo(current).LinkTarget;
		if (link is null) {
			if (File.Exists(current) || Directory.Exists(current) || _state.Staged is not null) {
				throw new InvalidDataException($"managed runner selector is missing or is not a symbolic link: {current}");
			}

			return;
		}

		string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(link, _root));
		string versions = Path.TrimEndingDirectorySeparator(Path.Combine(_root, "versions"));
		string buildName = Path.GetFileName(target);
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		if (!string.Equals(Path.GetDirectoryName(target), versions, comparison)
			|| !int.TryParse(buildName, out int build)
			|| !string.Equals(buildName, build.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)) {
			throw new InvalidDataException($"managed runner selector points outside versions/<build>: {current} -> {link}");
		}

		ReadVersionManifest(target, build);
		if (_state.Staged == build) {
			return;
		}

		_state = _state with { Staged = build, StagedDigest = null };
		_state.Save();
		_log($"adopted installed build {build}");
	}

	private static BundleManifest ReadVersionManifest(string versionDir, int expectedBuild) {
		string manifestPath = Path.Combine(versionDir, "manifest.json");
		if (!File.Exists(manifestPath) || !File.Exists(WorkerExecutable(versionDir))) {
			throw new InvalidDataException($"version {expectedBuild} is incomplete: {versionDir}");
		}

		var manifest = ReadManifest(manifestPath, $"version {expectedBuild} has an empty manifest");
		if (manifest.BuildNumber != expectedBuild) {
			throw new InvalidDataException(
				$"version directory {expectedBuild} contains manifest for build {manifest.BuildNumber}");
		}

		return manifest;
	}

	private void PruneLocked() {
		var keep = new HashSet<string>(StringComparer.Ordinal);
		if (_state.Staged is { } staged) {
			keep.Add(VersionDir(staged));
		}

		if (_state.ConfirmedGood is { } good) {
			keep.Add(VersionDir(good));
		}

		if (ManagedRunnerLayout.RootContaining(AppContext.BaseDirectory) is not null) {
			// The runner itself executes from a version dir; never delete the code we're running.
			keep.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory))));
		}

		foreach (string dir in Directory.GetDirectories(Path.Combine(_root, "versions"))) {
			string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
			if (keep.Any(k => full == Path.TrimEndingDirectorySeparator(Path.GetFullPath(k)) || k.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal))) {
				continue;
			}

			try {
				Directory.Delete(full, recursive: true);
				_log($"pruned old version {full}");
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				_log($"couldn't prune {full}: {ex.Message} (will retry after the next update)");
			}
		}
	}

	/// <summary>
	/// Extracts a downloaded layout tarball (gzip'd tar of <c>versions/&lt;build&gt;/…</c> + <c>current</c>)
	/// into a scratch dir and returns the manifest + the extracted version dir, ready for <see cref="Stage"/>.
	/// Throws when the archive doesn't carry exactly the expected shape.
	/// </summary>
	public static (BundleManifest Manifest, string VersionDir) ExtractBundle(string tarballPath, string scratchDir) {
		ArgumentException.ThrowIfNullOrEmpty(tarballPath);
		ArgumentException.ThrowIfNullOrEmpty(scratchDir);
		Directory.CreateDirectory(scratchDir);
		using (var file = File.OpenRead(tarballPath))
		using (var gunzip = new GZipStream(file, CompressionMode.Decompress)) {
			TarFile.ExtractToDirectory(gunzip, scratchDir, overwriteFiles: true);
		}

		string versionsDir = Path.Combine(scratchDir, "versions");
		string versionDir = Directory.Exists(versionsDir)
			? Directory.GetDirectories(versionsDir).SingleOrDefault()
				?? throw new InvalidDataException("bundle tarball holds no versions/<build>/ directory")
			: throw new InvalidDataException("bundle tarball holds no versions/ directory");

		string manifestPath = Path.Combine(versionDir, "manifest.json");
		if (!File.Exists(manifestPath)) {
			throw new InvalidDataException("bundle has no manifest.json — not an auto-update bundle");
		}

		var manifest = ReadManifest(manifestPath, "bundle manifest.json is empty");
		return (manifest, versionDir);
	}

	private static BundleManifest ReadManifest(string path, string emptyMessage) =>
		JsonSerializer.Deserialize<BundleManifest>(File.ReadAllText(path), ManifestJson)
		?? throw new InvalidDataException(emptyMessage);

	private sealed record StateFile {
		public int? Staged { get; init; }
		public string? StagedDigest { get; init; }
		public int? ConfirmedGood { get; init; }
		public IReadOnlyList<string> BadDigests { get; init; } = [];

		[System.Text.Json.Serialization.JsonIgnore]
		public string Path { get; init; } = "";

		public static StateFile Load(string path) {
			if (!File.Exists(path)) {
				return new StateFile { Path = path };
			}

			var loaded = JsonSerializer.Deserialize<StateFile>(
				File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
			return (loaded ?? new StateFile()) with { Path = path };
		}

		// Write-then-rename: a crash mid-write must not leave truncated JSON that fails the next boot's Load.
		public void Save() {
			string temp = Path + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
			File.Move(temp, Path, overwrite: true);
		}
	}
}
