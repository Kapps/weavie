using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Suggestions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="SuggestionService"/>: the bounded shallow manifest scan (root + ≤2 levels, skip-list,
/// .NET + JS/Cargo/Go/etc. manifests), the fail-open timeout, the setting gate, and snooze/dismiss filtering.
/// The workspace-setup suggestion (<see cref="CoreSuggestions"/>) is the subject under test.
/// </summary>
public sealed class SuggestionServiceTests : IDisposable {
	private const string SetupId = "workspace.setup";

	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-suggestion-tests", Guid.NewGuid().ToString("N"));

	public SuggestionServiceTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	[Theory]
	[InlineData("package.json")]
	[InlineData("Cargo.toml")]
	[InlineData("go.mod")]
	[InlineData("pyproject.toml")]
	[InlineData("Makefile")]
	[InlineData("App.csproj")]
	[InlineData("weavie.slnx")]
	public async Task ManifestAtRoot_IsRelevant(string manifest) {
		var fs = SeedFs(("/repo", manifest));

		var harness = await StartAsync(fs, "/repo", EmptySettings());

		Assert.Contains(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task ManifestUnderSubdir_IsRelevant() {
		// Weavie's own shape: the JS manifests live two levels down under src/web/, not at the root.
		var fs = SeedFs(("/repo/src/web", "package.json"));

		var harness = await StartAsync(fs, "/repo", EmptySettings());

		Assert.Contains(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task ManifestTooDeep_IsNotRelevant() {
		// Three levels down is past the shallow scan's reach — not a confident nudge.
		var fs = SeedFs(("/repo/a/b/c", "package.json"));

		var harness = await StartAsync(fs, "/repo", EmptySettings());

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task ManifestOnlyUnderSkippedDir_IsNotRelevant() {
		var fs = SeedFs(("/repo/node_modules", "package.json"));

		var harness = await StartAsync(fs, "/repo", EmptySettings());

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task NoManifest_IsNotRelevant() {
		var fs = SeedFs(("/repo", "readme.txt"));

		var harness = await StartAsync(fs, "/repo", EmptySettings());

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task OnlySetupCommandSet_StillRelevant_TestProfileMissing() {
		// The card offers to configure BOTH knowledge-shaped settings, so it stays until each is configured.
		var fs = SeedFs(("/repo", "package.json"));

		var harness = await StartAsync(fs, "/repo", SettingsWith("worktree.setupCommand = \"pnpm install\""));

		Assert.Contains(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task OnlyTestProfileSet_StillRelevant_SetupCommandMissing() {
		var fs = SeedFs(("/repo", "package.json"));

		var harness = await StartAsync(fs, "/repo", SettingsWith("test.profile = '[]'"));

		Assert.Contains(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task BothConfigured_IsNotRelevant() {
		// An explicit empty test profile ([]) counts as configured ("this repo has no tests"), like a set command.
		var fs = SeedFs(("/repo", "package.json"));

		var harness = await StartAsync(fs, "/repo",
			SettingsWith("worktree.setupCommand = \"pnpm install\"\ntest.profile = '[]'"));

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task LegacyWorktreeDismissal_AlsoSilencesWorkspaceSetup() {
		// A user who dismissed the old worktree-only card forever isn't re-nagged by its successor.
		var fs = SeedFs(("/repo", "package.json"));
		var dismissals = new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json");
		dismissals.Add("worktree.setupCommand");

		var harness = await StartAsync(fs, "/repo", EmptySettings(), dismissals);

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task Snooze_RemovesFromActiveSet() {
		var fs = SeedFs(("/repo", "package.json"));
		var harness = await StartAsync(fs, "/repo", EmptySettings());
		Assert.Contains(SetupId, harness.ActiveIds());

		harness.Service.Snooze(SetupId);

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task DismissForever_RemovesAndPersists() {
		var fs = SeedFs(("/repo", "package.json"));
		var harness = await StartAsync(fs, "/repo", EmptySettings());

		harness.Service.DismissForever(SetupId);

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
		Assert.True(harness.Dismissals.IsDismissed(SetupId));
	}

	[Fact]
	public async Task DismissedBeforeStart_NeverOffered() {
		var fs = SeedFs(("/repo", "package.json"));
		var dismissals = new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json");
		dismissals.Add(SetupId);

		var harness = await StartAsync(fs, "/repo", EmptySettings(), dismissals);

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task SlowScan_TimesOut_FailsOpen() {
		// A scan too slow to finish in time fails open: the dismissible card shows rather than vanishing silently.
		// SlowFileSystem blocks its directory read until disposed, so the probe's timeout ALWAYS wins the race —
		// a fixed Sleep can lose to a Task.Delay whose continuation is starved under parallel-test threadpool load,
		// letting the (empty-fs) scan win and flip the result to "no manifest" (the historical flake).
		using var fs = new SlowFileSystem();
		var harness = await StartAsync(fs, "/repo", EmptySettings(),
			new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json"), TimeSpan.FromMilliseconds(200));

		Assert.Contains(SetupId, harness.ActiveIds());
	}

	private static InMemoryFileSystem SeedFs(params (string Dir, string File)[] entries) {
		var fs = new InMemoryFileSystem();
		foreach (var (dir, file) in entries) {
			fs.WriteAllText(Path.Combine(dir, file), "x");
		}

		return fs;
	}

	private SettingsStore EmptySettings() =>
		new(CoreSettings.CreateRegistry(), Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".toml"), enableWatcher: false, _ => Path.Combine(_dir, "ws-settings.toml"));

	private SettingsStore SettingsWith(string toml) {
		string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".toml");
		File.WriteAllText(path, toml + "\n");
		return new SettingsStore(CoreSettings.CreateRegistry(), path, enableWatcher: false, _ => Path.Combine(_dir, "ws-settings.toml"));
	}

	// Generous probe timeout for the fast in-memory fs: the scan always wins the race, so the manifest result is
	// deterministic under any test-runner load. A short timeout can lose to threadpool starvation under parallel
	// test load and fail open (the suggestion appears), flaking the "not relevant" cases.
	private static readonly TimeSpan FastProbe = TimeSpan.FromSeconds(30);

	private static Task<Harness> StartAsync(IFileSystem fs, string root, SettingsStore settings) =>
		StartAsync(fs, root, settings, new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json"));

	private static Task<Harness> StartAsync(IFileSystem fs, string root, SettingsStore settings, SuggestionDismissals dismissals) =>
		StartAsync(fs, root, settings, dismissals, FastProbe);

	private static async Task<Harness> StartAsync(
		IFileSystem fs, string root, SettingsStore settings, SuggestionDismissals dismissals, TimeSpan probeTimeout) {
		var pushes = new List<IReadOnlyList<SuggestionDefinition>>();
		var first = new TaskCompletionSource();
		var gate = new Lock();
		void Push(IReadOnlyList<SuggestionDefinition> active) {
			lock (gate) {
				pushes.Add(active);
			}

			first.TrySetResult();
		}

		var service = new SuggestionService(CoreSuggestions.CreateRegistry(), settings, fs, root, dismissals, probeTimeout, Push);
		await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
		IReadOnlyList<string> ActiveIds() {
			lock (gate) {
				return [.. pushes[^1].Select(d => d.Id)];
			}
		}

		return new Harness(service, ActiveIds, dismissals);
	}

	private sealed record Harness(SuggestionService Service, Func<IReadOnlyList<string>> ActiveIds, SuggestionDismissals Dismissals);

	// Blocks the directory walk until disposed, so the probe's timeout deterministically wins the WhenAny race
	// (no wall-clock dependency that a starved Task.Delay could lose), exercising the fail-open path.
	private sealed class SlowFileSystem : IFileSystem, IDisposable {
		private readonly InMemoryFileSystem _inner = new();
		private readonly ManualResetEventSlim _release = new(false);

		public bool FileExists(string path) => _inner.FileExists(path);
		public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
		public bool TryGetStat(string path, out FileStat stat) => _inner.TryGetStat(path, out stat);
		public string ReadAllText(string path) => _inner.ReadAllText(path);
		public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
		public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);
		public void WriteAllBytes(string path, byte[] contents) => _inner.WriteAllBytes(path, contents);
		public void WriteAllTextAtomic(string path, string contents) => _inner.WriteAllTextAtomic(path, contents);
		public void DeleteFile(string path) => _inner.DeleteFile(path);

		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) {
			_release.Wait(); // the scan can never finish before the probe times out, so fail-open is deterministic
			return _inner.EnumerateDirectory(path);
		}

		public void Dispose() => _release.Set();
	}
}
