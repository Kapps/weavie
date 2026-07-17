using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Suggestions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="SuggestionService"/>: the manifest gate (via the injected probe), the fail-open timeout,
/// the setting gate, and snooze/dismiss filtering. The manifest walk itself now lives in
/// <c>WorkspaceDetector</c> (see WorkspaceDetectorTests); here the probe is a stub. The workspace-setup
/// suggestion (<see cref="CoreSuggestions"/>) is the subject under test.
/// </summary>
public sealed class SuggestionServiceTests : IDisposable {
	private const string SetupId = "workspace.setup";
	private const string LearnId = "corrections.learn";
	private static readonly Func<bool> ManifestPresent = () => true;
	private static readonly Func<bool> NoManifest = () => false;

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

	[Fact]
	public async Task ManifestPresent_Unconfigured_IsRelevant() {
		var harness = await StartAsync(EmptySettings(), ManifestPresent);

		Assert.Contains(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task NoManifest_IsNotRelevant() {
		var harness = await StartAsync(EmptySettings(), NoManifest);

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task OnlySetupCommandSet_StillRelevant_TestProfileMissing() {
		// The card offers to configure BOTH knowledge-shaped settings, so it stays until each is configured.
		var harness = await StartAsync(SettingsWith("worktree.setupCommand = \"pnpm install\""), ManifestPresent);

		Assert.Contains(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task OnlyTestProfileSet_StillRelevant_SetupCommandMissing() {
		var harness = await StartAsync(SettingsWith("test.profile = '[]'"), ManifestPresent);

		Assert.Contains(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task BothConfigured_IsNotRelevant() {
		// An explicit empty test profile ([]) counts as configured ("this repo has no tests"), like a set command.
		var harness = await StartAsync(
			SettingsWith("worktree.setupCommand = \"pnpm install\"\ntest.profile = '[]'"), ManifestPresent);

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task LegacyWorktreeDismissal_AlsoSilencesWorkspaceSetup() {
		// A user who dismissed the old worktree-only card forever isn't re-nagged by its successor.
		var dismissals = new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json");
		dismissals.Add("worktree.setupCommand");

		var harness = await StartAsync(EmptySettings(), ManifestPresent, dismissals);

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task Snooze_RemovesFromActiveSet() {
		var harness = await StartAsync(EmptySettings(), ManifestPresent);
		Assert.Contains(SetupId, harness.ActiveIds());

		harness.Service.Snooze(SetupId);

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task DismissForever_RemovesAndPersists() {
		var harness = await StartAsync(EmptySettings(), ManifestPresent);

		harness.Service.DismissForever(SetupId);

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
		Assert.True(harness.Dismissals.IsDismissed(SetupId));
	}

	[Fact]
	public async Task DismissedBeforeStart_NeverOffered() {
		var dismissals = new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json");
		dismissals.Add(SetupId);

		var harness = await StartAsync(EmptySettings(), ManifestPresent, dismissals);

		Assert.DoesNotContain(SetupId, harness.ActiveIds());
	}

	[Fact]
	public async Task SlowProbe_TimesOut_FailsOpen() {
		// A probe too slow to finish fails open: the dismissible card shows rather than vanishing silently. The
		// probe blocks until released, so the timeout ALWAYS wins the race — no wall-clock dependency a starved
		// Task.Delay could lose (the historical flake). It would return "no manifest" if it ever completed in time.
		using var gate = new ManualResetEventSlim(false);
		var harness = await StartAsync(EmptySettings(), () => { gate.Wait(); return false; },
			new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json"), TimeSpan.FromMilliseconds(200));

		Assert.Contains(SetupId, harness.ActiveIds());
		gate.Set(); // release the blocked probe thread
	}

	[Fact]
	public async Task CorrectionsBelowThreshold_LearnCardNotOffered() {
		var harness = await StartAsync(EmptySettings(), NoManifest, static () => 9);

		Assert.DoesNotContain(LearnId, harness.ActiveIds());
	}

	[Fact]
	public async Task CorrectionsAtThreshold_LearnCardOffered() {
		var harness = await StartAsync(EmptySettings(), NoManifest, static () => 10);

		Assert.Contains(LearnId, harness.ActiveIds());
	}

	[Fact]
	public async Task InstallableServerMiss_OffersItsCard_WithTheServerArg() {
		var harness = await StartAsync(EmptySettings(), NoManifest, static () => (string[])["go"]);

		Assert.Contains("lsp.install.go", harness.ActiveIds());
		Assert.DoesNotContain("lsp.install.typescript", harness.ActiveIds());
		var install = harness.Active().Single(d => d.Id == "lsp.install.go").Actions[0];
		Assert.Equal(CoreCommands.InstallLanguageServer, install.CommandId);
		Assert.Equal("""{"server":"go"}""", install.ArgsJson);
	}

	[Fact]
	public async Task InstallableServers_ReadFreshEachEvaluate() {
		// The set shrinks the moment an install lands (Recompute + Evaluate): the card must go with it.
		IReadOnlyCollection<string> installable = ["typescript"];
		var harness = await StartAsync(EmptySettings(), NoManifest, () => installable);
		Assert.Contains("lsp.install.typescript", harness.ActiveIds());

		installable = [];
		harness.Service.Evaluate();

		Assert.DoesNotContain("lsp.install.typescript", harness.ActiveIds());
	}

	[Fact]
	public async Task InstallCard_DismissForever_IsPerLanguage() {
		var harness = await StartAsync(EmptySettings(), NoManifest, static () => (string[])["go", "csharp"]);

		harness.Service.DismissForever("lsp.install.go");

		Assert.DoesNotContain("lsp.install.go", harness.ActiveIds());
		Assert.Contains("lsp.install.csharp", harness.ActiveIds());
	}

	[Fact]
	public async Task CorrectionCount_ReadFreshEachEvaluate() {
		// Unlike the one-shot manifest probe, the ring's count changes over time — each Evaluate re-reads the
		// supplier, so the card appears the moment an append crosses the (here raised) threshold.
		int count = 4;
		var harness = await StartAsync(SettingsWith("corrections.learnThreshold = 5"), NoManifest, () => count);
		Assert.DoesNotContain(LearnId, harness.ActiveIds());

		count = 5;
		harness.Service.Evaluate();

		Assert.Contains(LearnId, harness.ActiveIds());
	}

	private SettingsStore EmptySettings() =>
		new(CoreSettings.CreateRegistry(), Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".toml"), enableWatcher: false, _ => Path.Combine(_dir, "ws-settings.toml"));

	private SettingsStore SettingsWith(string toml) {
		string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".toml");
		File.WriteAllText(path, toml + "\n");
		return new SettingsStore(CoreSettings.CreateRegistry(), path, enableWatcher: false, _ => Path.Combine(_dir, "ws-settings.toml"));
	}

	// Generous probe timeout so a stub probe that returns immediately always wins the race — deterministic under
	// parallel test load. A short timeout could lose to threadpool starvation and fail open, flaking negatives.
	private static readonly TimeSpan FastProbe = TimeSpan.FromSeconds(30);

	private static Task<Harness> StartAsync(SettingsStore settings, Func<bool> probe) =>
		StartAsync(settings, probe, new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json"));

	private static Task<Harness> StartAsync(SettingsStore settings, Func<bool> probe, SuggestionDismissals dismissals) =>
		StartAsync(settings, probe, dismissals, FastProbe, static () => 0, static () => []);

	private static Task<Harness> StartAsync(
		SettingsStore settings, Func<bool> probe, SuggestionDismissals dismissals, TimeSpan probeTimeout) =>
		StartAsync(settings, probe, dismissals, probeTimeout, static () => 0, static () => []);

	private static Task<Harness> StartAsync(SettingsStore settings, Func<bool> probe, Func<int> pendingCorrections) =>
		StartAsync(settings, probe, new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json"),
			FastProbe, pendingCorrections, static () => []);

	private static Task<Harness> StartAsync(
		SettingsStore settings, Func<bool> probe, Func<IReadOnlyCollection<string>> installableServers) =>
		StartAsync(settings, probe, new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json"),
			FastProbe, static () => 0, installableServers);

	private static async Task<Harness> StartAsync(
		SettingsStore settings, Func<bool> probe, SuggestionDismissals dismissals, TimeSpan probeTimeout,
		Func<int> pendingCorrections, Func<IReadOnlyCollection<string>> installableServers) {
		var pushes = new List<IReadOnlyList<SuggestionDefinition>>();
		var first = new TaskCompletionSource();
		var gate = new Lock();
		void Push(IReadOnlyList<SuggestionDefinition> active) {
			lock (gate) {
				pushes.Add(active);
			}

			first.TrySetResult();
		}

		var service = new SuggestionService(
			CoreSuggestions.CreateRegistry(), settings, new InMemoryFileSystem(), "/repo", dismissals, probeTimeout,
			Push, probe, pendingCorrections, installableServers);
		await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
		IReadOnlyList<SuggestionDefinition> Active() {
			lock (gate) {
				return pushes[^1];
			}
		}

		return new Harness(service, () => [.. Active().Select(d => d.Id)], Active, dismissals);
	}

	private sealed record Harness(
		SuggestionService Service,
		Func<IReadOnlyList<string>> ActiveIds,
		Func<IReadOnlyList<SuggestionDefinition>> Active,
		SuggestionDismissals Dismissals);
}
