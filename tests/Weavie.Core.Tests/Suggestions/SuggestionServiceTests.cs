using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Suggestions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="SuggestionService"/>: the bounded shallow manifest scan (root + ≤2 levels, skip-list,
/// .NET + JS/Cargo/Go/etc. manifests), the fail-open timeout, the setting gate, and snooze/dismiss filtering.
/// The worktree-setup suggestion (<see cref="CoreSuggestions"/>) is the subject under test.
/// </summary>
public sealed class SuggestionServiceTests : IDisposable {
	private const string WorktreeId = "worktree.setupCommand";

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

		Assert.Contains(WorktreeId, harness.ActiveIds());
	}

	[Fact]
	public async Task ManifestUnderSubdir_IsRelevant() {
		// Weavie's own shape: the JS manifests live two levels down under src/web/, not at the root.
		var fs = SeedFs(("/repo/src/web", "package.json"));

		var harness = await StartAsync(fs, "/repo", EmptySettings());

		Assert.Contains(WorktreeId, harness.ActiveIds());
	}

	[Fact]
	public async Task ManifestTooDeep_IsNotRelevant() {
		// Three levels down is past the shallow scan's reach — not a confident nudge.
		var fs = SeedFs(("/repo/a/b/c", "package.json"));

		var harness = await StartAsync(fs, "/repo", EmptySettings());

		Assert.DoesNotContain(WorktreeId, harness.ActiveIds());
	}

	[Fact]
	public async Task ManifestOnlyUnderSkippedDir_IsNotRelevant() {
		var fs = SeedFs(("/repo/node_modules", "package.json"));

		var harness = await StartAsync(fs, "/repo", EmptySettings());

		Assert.DoesNotContain(WorktreeId, harness.ActiveIds());
	}

	[Fact]
	public async Task NoManifest_IsNotRelevant() {
		var fs = SeedFs(("/repo", "readme.txt"));

		var harness = await StartAsync(fs, "/repo", EmptySettings());

		Assert.DoesNotContain(WorktreeId, harness.ActiveIds());
	}

	[Fact]
	public async Task SettingAlreadySet_IsNotRelevant() {
		var fs = SeedFs(("/repo", "package.json"));

		var harness = await StartAsync(fs, "/repo", SettingsWith("worktree.setupCommand = \"pnpm install\""));

		Assert.DoesNotContain(WorktreeId, harness.ActiveIds());
	}

	[Fact]
	public async Task Snooze_RemovesFromActiveSet() {
		var fs = SeedFs(("/repo", "package.json"));
		var harness = await StartAsync(fs, "/repo", EmptySettings());
		Assert.Contains(WorktreeId, harness.ActiveIds());

		harness.Service.Snooze(WorktreeId);

		Assert.DoesNotContain(WorktreeId, harness.ActiveIds());
	}

	[Fact]
	public async Task DismissForever_RemovesAndPersists() {
		var fs = SeedFs(("/repo", "package.json"));
		var harness = await StartAsync(fs, "/repo", EmptySettings());

		harness.Service.DismissForever(WorktreeId);

		Assert.DoesNotContain(WorktreeId, harness.ActiveIds());
		Assert.True(harness.Dismissals.IsDismissed(WorktreeId));
	}

	[Fact]
	public async Task DismissedBeforeStart_NeverOffered() {
		var fs = SeedFs(("/repo", "package.json"));
		var dismissals = new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json");
		dismissals.Add(WorktreeId);

		var harness = await StartAsync(fs, "/repo", EmptySettings(), dismissals);

		Assert.DoesNotContain(WorktreeId, harness.ActiveIds());
	}

	[Fact]
	public async Task SlowScan_TimesOut_FailsOpen() {
		// A scan too slow to finish in time fails open: the dismissible card shows rather than vanishing silently.
		var harness = await StartAsync(new SlowFileSystem(), "/repo", EmptySettings());

		Assert.Contains(WorktreeId, harness.ActiveIds());
	}

	private static InMemoryFileSystem SeedFs(params (string Dir, string File)[] entries) {
		var fs = new InMemoryFileSystem();
		foreach (var (dir, file) in entries) {
			fs.WriteAllText(Path.Combine(dir, file), "x");
		}

		return fs;
	}

	private SettingsStore EmptySettings() =>
		new(CoreSettings.CreateRegistry(), Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".toml"), enableWatcher: false);

	private SettingsStore SettingsWith(string toml) {
		string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".toml");
		File.WriteAllText(path, toml + "\n");
		return new SettingsStore(CoreSettings.CreateRegistry(), path, enableWatcher: false);
	}

	private static Task<Harness> StartAsync(IFileSystem fs, string root, SettingsStore settings) =>
		StartAsync(fs, root, settings, new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json"));

	private static async Task<Harness> StartAsync(IFileSystem fs, string root, SettingsStore settings, SuggestionDismissals dismissals) {
		var pushes = new List<IReadOnlyList<SuggestionDefinition>>();
		var first = new TaskCompletionSource();
		var gate = new Lock();
		void Push(IReadOnlyList<SuggestionDefinition> active) {
			lock (gate) {
				pushes.Add(active);
			}

			first.TrySetResult();
		}

		var service = new SuggestionService(CoreSuggestions.CreateRegistry(), settings, fs, root, dismissals, Push);
		await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
		IReadOnlyList<string> ActiveIds() {
			lock (gate) {
				return [.. pushes[^1].Select(d => d.Id)];
			}
		}

		return new Harness(service, ActiveIds, dismissals);
	}

	private sealed record Harness(SuggestionService Service, Func<IReadOnlyList<string>> ActiveIds, SuggestionDismissals Dismissals);

	// Blocks the directory walk past the probe's wall-clock timeout, to exercise the fail-open path.
	private sealed class SlowFileSystem : IFileSystem {
		private readonly InMemoryFileSystem _inner = new();

		public bool FileExists(string path) => _inner.FileExists(path);
		public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
		public bool TryGetStat(string path, out FileStat stat) => _inner.TryGetStat(path, out stat);
		public string ReadAllText(string path) => _inner.ReadAllText(path);
		public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);
		public void WriteAllTextAtomic(string path, string contents) => _inner.WriteAllTextAtomic(path, contents);
		public void DeleteFile(string path) => _inner.DeleteFile(path);

		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) {
			Thread.Sleep(1500); // outlast the 500ms probe timeout
			return _inner.EnumerateDirectory(path);
		}
	}
}
