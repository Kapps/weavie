using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Suggestions;
using Weavie.Core.Workflows;
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
	private const string DecisionModelId = "decisions.model";
	private static readonly SuggestionSignals NoSignals = new(0, new DecisionHistorySummary(0, TimeSpan.Zero));
	private static readonly Func<bool> ManifestPresent = () => true;
	private static readonly Func<bool> NoManifest = () => false;

	private readonly TempDirectory _dir = new("weavie-suggestion-tests");

	public void Dispose() => _dir.Dispose();

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
		var harness = await StartAsync(SettingsWith("corrections.enabled = true"), NoManifest, static () => 9);

		Assert.DoesNotContain(LearnId, harness.ActiveIds());
	}

	[Fact]
	public async Task CorrectionsAtThreshold_LearnCardRequiresOptIn() {
		var settings = EmptySettings();
		var harness = await StartAsync(settings, NoManifest, static () => 10);
		Assert.DoesNotContain(LearnId, harness.ActiveIds());
		settings.Set(CorrectionsSettings.Enabled, System.Text.Json.JsonSerializer.SerializeToElement(true));
		harness.Service.Evaluate();

		Assert.Contains(LearnId, harness.ActiveIds());
	}

	[Fact]
	public async Task CorrectionCount_ReadFreshEachEvaluate() {
		// Unlike the one-shot manifest probe, the ring's count changes over time — each Evaluate re-reads the
		// supplier, so the card appears the moment an append crosses the (here raised) threshold.
		int count = 4;
		var harness = await StartAsync(SettingsWith("corrections.enabled = true\ncorrections.learnThreshold = 5"), NoManifest, () => count);
		Assert.DoesNotContain(LearnId, harness.ActiveIds());

		count = 5;
		harness.Service.Evaluate();

		Assert.Contains(LearnId, harness.ActiveIds());
	}

	[Theory]
	[InlineData(5, 1340, "agent", true)]
	[InlineData(5, 1000, "agent", true)]
	[InlineData(5, 999, "agent", false)]
	[InlineData(4, 1340, "agent", false)]
	[InlineData(12, 1340, "typesafe", false)]
	public async Task DecisionModelCard_NeedsFiveSlowAgentAnsweredDecisionsWhileTheAgentDecides(
		int answered, int medianMs, string provider, bool offered) {
		var settings = SettingsWith($"inference.decisionProvider = \"{provider}\"");

		var harness = await StartAsync(settings, NoManifest, () => Decisions(answered, medianMs));

		Assert.Equal(offered, harness.ActiveIds().Contains(DecisionModelId));
	}

	[Fact]
	public async Task DecisionModelCard_QuotesTheCountAndMedianAndRunsChooseDecisionModel() {
		var history = Decisions(12, 1340);
		var harness = await StartAsync(EmptySettings(), NoManifest, () => history);
		history = Decisions(13, 1260);
		harness.Service.Evaluate();

		var card = Assert.Single(harness.Active(), active => active.Definition.Id == DecisionModelId);
		Assert.Equal(
			"Your last 13 workflow decisions took ~1.3s each through the agent. A decision model answers in ~0.2s with calibrated confidence.",
			card.Body);
		Assert.Equal(CoreCommands.ChooseDecisionProvider, card.Definition.Actions[0].CommandId);
	}

	private static SuggestionSignals Decisions(int agentAnswered, int medianMs) =>
		NoSignals with { WorkflowDecisions = new DecisionHistorySummary(agentAnswered, TimeSpan.FromMilliseconds(medianMs)) };

	private SettingsStore EmptySettings() =>
		new(CoreSettings.CreateRegistry(), _dir.Combine(Guid.NewGuid().ToString("N") + ".toml"), enableWatcher: false, _ => _dir.Combine("ws-settings.toml"));

	private SettingsStore SettingsWith(string toml) {
		string path = _dir.WriteFile(Guid.NewGuid().ToString("N") + ".toml", toml + "\n");
		return new SettingsStore(CoreSettings.CreateRegistry(), path, enableWatcher: false, _ => _dir.Combine("ws-settings.toml"));
	}

	// Generous probe timeout so a stub probe that returns immediately always wins the race — deterministic under
	// parallel test load. A short timeout could lose to threadpool starvation and fail open, flaking negatives.
	private static readonly TimeSpan FastProbe = TimeSpan.FromSeconds(30);

	private static Task<Harness> StartAsync(SettingsStore settings, Func<bool> probe) =>
		StartAsync(settings, probe, new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json"));

	private static Task<Harness> StartAsync(SettingsStore settings, Func<bool> probe, SuggestionDismissals dismissals) =>
		StartAsync(settings, probe, dismissals, FastProbe, static () => NoSignals);

	private static Task<Harness> StartAsync(
		SettingsStore settings, Func<bool> probe, SuggestionDismissals dismissals, TimeSpan probeTimeout) =>
		StartAsync(settings, probe, dismissals, probeTimeout, static () => NoSignals);

	private static Task<Harness> StartAsync(SettingsStore settings, Func<bool> probe, Func<int> pendingCorrections) =>
		StartAsync(settings, probe, () => NoSignals with { PendingCorrectionCount = pendingCorrections() });

	private static Task<Harness> StartAsync(SettingsStore settings, Func<bool> probe, Func<SuggestionSignals> signals) =>
		StartAsync(settings, probe, new SuggestionDismissals(new InMemoryFileSystem(), "/state/suggestions.json"),
			FastProbe, signals);

	private static async Task<Harness> StartAsync(
		SettingsStore settings, Func<bool> probe, SuggestionDismissals dismissals, TimeSpan probeTimeout,
		Func<SuggestionSignals> signals) {
		var pushes = new List<IReadOnlyList<ActiveSuggestion>>();
		var first = new TaskCompletionSource();
		var gate = new Lock();
		void Push(IReadOnlyList<ActiveSuggestion> active) {
			lock (gate) {
				pushes.Add(active);
			}

			first.TrySetResult();
		}

		var service = new SuggestionService(
			CoreSuggestions.CreateRegistry(), settings, new InMemoryFileSystem(), "/repo", dismissals, probeTimeout,
			Push, probe, signals);
		await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
		IReadOnlyList<ActiveSuggestion> Active() {
			lock (gate) {
				return pushes[^1];
			}
		}

		return new Harness(service, Active, dismissals);
	}

	private sealed record Harness(SuggestionService Service, Func<IReadOnlyList<ActiveSuggestion>> Active, SuggestionDismissals Dismissals) {
		public IReadOnlyList<string> ActiveIds() => [.. Active().Select(active => active.Definition.Id)];
	}
}
