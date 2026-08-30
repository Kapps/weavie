using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core;
using Weavie.Core.Corrections;
using Weavie.Core.Hooks;
using Weavie.Core.Inference;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Learn-from-corrections over a real <see cref="HostCore"/>: hook-driven agent turns whose output the user then
/// edits in the editor (an <c>fs-write</c>, captured at the save) accumulate in the workspace ring, the
/// <c>corrections.learn</c> card surfaces at the threshold, and the command runs ONE isolated inference query and
/// renders its proposed rules into the read-only <c>about:corrections</c> tab — nothing is typed into the agent.
/// A success consumes the ring and starts the 24-hour interval; an empty ring and a failed analysis are both loud
/// and cost nothing.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreLearnTests {
	private const string LearnCommand = "weavie.learn.fromCorrections";
	private const string LearnTarget = "about:corrections";

	[Fact]
	public async Task CorrectionsAccumulate_CardSurfaces_AndLearnRendersProposedRules_ThenConsumesRing() {
		var inference = new LearnInferenceStub(new InferenceSuccess<CorrectionLessonsOutput> {
			Value = new CorrectionLessonsOutput {
				Rules = [new CorrectionRule { Rule = "Prefer <em>tabs</em>", Evidence = "corrections 1 and 2" }],
				Summary = "The user keeps re-indenting.",
			},
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(
			repo => File.WriteAllText(Path.Combine(repo, "AGENTS.md"), "Existing rule: never add fallbacks.\n"),
			_ => inference);
		var session = host.SelectedSession;
		session.Claude!.EnsureStarted();
		var claude = Assert.Single(host.Platform.NoopLauncher.Created);
		string file = Path.Combine(host.RepoRoot, "app.cs");
		host.Settings.Set("corrections.learnThreshold", JsonDocument.Parse("3").RootElement);

		// Four corrected turns: the agent edits, then the user edits the agent's output in the editor — an
		// fs-write that captures the correction at the save (not a boundary). Each turn is a distinct region, so
		// the four stay four (coalescing only collapses re-saves of one region); three meets the pinned threshold.
		for (int turn = 1; turn <= 4; turn++) {
			Boundary(session, $"prompt {turn}");
			AgentEdit(session, file, $"agent version {turn}\n");
			await HandEditAsync(host, session, file, $"user version {turn}\n");
			if (turn == 3) {
				await WaitForSuggestionAsync(host, present: true);
			}
		}

		var result = await host.InvokeClientCommandAsync(LearnCommand, new { });
		Assert.True(result.Ok, result.Error);

		// The tab opens spinning, then resolves to the model's proposal — the whole result, never a prompt to send.
		Assert.Equal(LearnTarget, Source(host, session, "loading")!.Value.GetProperty("target").GetString());
		var document = await WaitForSourceAsync(host, session, "document");
		string html = document.GetProperty("html").GetString()!;
		Assert.Equal(LearnTarget, document.GetProperty("target").GetString());
		Assert.Equal("corrections", document.GetProperty("sourceId").GetString());
		Assert.Contains("The user keeps re-indenting.", html, StringComparison.Ordinal);
		Assert.Contains("corrections 1 and 2", html, StringComparison.Ordinal);
		// Weavie edits nothing, so the rules also arrive as one paste-ready block.
		Assert.Contains("<pre>- Prefer &lt;em&gt;tabs&lt;/em&gt;\n</pre>", html, StringComparison.Ordinal);
		// The model's own words reach an innerHTML sink, so its markup is text, not structure.
		Assert.Contains("Prefer &lt;em&gt;tabs&lt;/em&gt;", html, StringComparison.Ordinal);
		Assert.Equal(string.Empty, claude.WrittenText); // nothing is typed into the agent

		// One isolated reasoning query, carrying the corpus and the repository's existing instructions.
		Assert.Equal(1, inference.Calls);
		Assert.Equal(InferenceModelCategory.Reasoning, inference.Category);
		Assert.Equal(InferenceInvocationOrigin.UserInitiated, inference.Origin);
		Assert.Equal(host.RepoRoot, inference.Workspace);
		Assert.Contains("never add fallbacks", inference.Prompt!, StringComparison.Ordinal);
		Assert.Contains("prompt 1", inference.Prompt!, StringComparison.Ordinal);
		Assert.Contains("-agent version 1", inference.Prompt!, StringComparison.Ordinal);
		Assert.Contains("+user version 1", inference.Prompt!, StringComparison.Ordinal);

		// The analyzed entries were consumed: the persisted ring is empty and the card withdrew.
		Assert.Equal(string.Empty, File.ReadAllText(RingPath(host)));
		await WaitForSuggestionAsync(host, present: false);
	}

	[Fact]
	public async Task WithinTwentyFourHours_NoSecondAnalysisRuns_AndTheLastOneIsReopenedInstead() {
		var inference = new LearnInferenceStub(new InferenceSuccess<CorrectionLessonsOutput> {
			Value = new CorrectionLessonsOutput {
				Rules = [new CorrectionRule { Rule = "Keep it", Evidence = "correction 1" }],
				Summary = "Nothing else durable.",
			},
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);
		var session = host.SelectedSession;
		await CorrectAsync(host, session, "first", 1);

		Assert.True((await host.InvokeClientCommandAsync(LearnCommand, new { })).Ok);
		await WaitForSourceAsync(host, session, "document");
		await CorrectAsync(host, session, "second", 2);

		var again = await host.InvokeClientCommandAsync(LearnCommand, new { });

		// The interval holds — no second query — but the day's result is kept, so the refusal is not a dead end
		// even though the ring that produced it is gone.
		Assert.True(again.Ok, again.Error);
		Assert.Contains("once every 24 hours", again.Message, StringComparison.Ordinal);
		Assert.Contains("Reopened your most recent analysis", again.Message, StringComparison.Ordinal);
		Assert.Equal(1, inference.Calls);
		Assert.Contains("Keep it", Source(host, session, "document")!.Value.GetProperty("html").GetString()!, StringComparison.Ordinal);
		// The correction recorded after the analysis is untouched, still waiting for tomorrow.
		Assert.Equal(1, RingCount(host));
	}

	[Fact]
	public async Task FailedAnalysis_ShowsTheReasonInTheTab_AndSpendsNeitherTheRingNorTheDay() {
		var inference = new LearnInferenceStub(new InferenceFailure<CorrectionLessonsOutput> {
			Kind = InferenceFailureKind.Disabled,
			Detail = "Ad-hoc inference is disabled.",
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);
		var session = host.SelectedSession;
		await CorrectAsync(host, session, "first", 1);

		Assert.True((await host.InvokeClientCommandAsync(LearnCommand, new { })).Ok);

		var error = await WaitForSourceAsync(host, session, "error");
		Assert.Contains("Ad-hoc inference is disabled.", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
		Assert.Equal(1, RingCount(host)); // the corpus survives a failure …
		Assert.True((await host.InvokeClientCommandAsync(LearnCommand, new { })).Ok); // … and so does the day
		Assert.Equal(2, inference.Calls);
	}

	[Fact]
	public async Task EmptyRing_FailsLoudly_WithoutQueryingTheModel() {
		var inference = new LearnInferenceStub(new InferenceSuccess<CorrectionLessonsOutput> {
			Value = new CorrectionLessonsOutput { Rules = [], Summary = "unused" },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);
		host.SelectedSession.Claude!.EnsureStarted();

		var result = await host.InvokeClientCommandAsync(LearnCommand, new { });

		Assert.False(result.Ok);
		Assert.Contains("No corrections recorded", result.Error, StringComparison.Ordinal);
		Assert.Equal(0, inference.Calls);
		Assert.Null(Source(host, host.SelectedSession, "loading"));
	}

	private static string RingPath(TestHost host) =>
		WeaviePaths.WorkspaceCorrectionsFile(WorkspaceId.ForPath(host.RepoRoot));

	private static int RingCount(TestHost host) =>
		File.ReadAllLines(RingPath(host)).Count(line => line.Length > 0);

	private static InferenceReceipt Receipt() => new() {
		ProviderId = "test",
		Category = InferenceModelCategory.Reasoning,
		ModelId = "reasoning-model",
		Duration = TimeSpan.FromSeconds(2),
	};

	// One corrected turn: the agent writes, the user saves over it.
	private static async Task CorrectAsync(TestHost host, HostSession session, string prompt, int turn) {
		string file = Path.Combine(host.RepoRoot, $"app{turn}.cs");
		Boundary(session, prompt);
		AgentEdit(session, file, $"agent {turn}\n");
		await HandEditAsync(host, session, file, $"user {turn}\n");
	}

	private static void Boundary(HostSession session, string prompt) =>
		session.ObserveHook(new HookRequest {
			Event = HookEventKind.UserPromptSubmit,
			ToolName = string.Empty,
			ToolInputJson = "{}",
			SessionId = "claude-1",
			Prompt = prompt,
		});

	private static void AgentEdit(HostSession session, string file, string content) {
		var edit = new HookRequest {
			Event = HookEventKind.PreToolUse,
			ToolName = "Edit",
			ToolInputJson = JsonSerializer.Serialize(new { file_path = file }),
			SessionId = "claude-1",
		};
		session.ObserveHook(edit);
		File.WriteAllText(file, content);
		session.ObserveHook(edit with { Event = HookEventKind.PostToolUse });
	}

	// The user editing the agent's output in the editor: an fs-write (as the file provider posts on autosave),
	// which writes disk AND captures the correction at the save.
	private static async Task HandEditAsync(
		TestHost host,
		HostSession session,
		string file,
		string content) {
		var result = await host.SessionRequestAsync<JsonElement>(
			session,
			"files",
			"write",
			new { path = file, content });
		Assert.True(result.GetProperty("ok").GetBoolean());
	}

	private static JsonElement? Source(TestHost host, HostSession session, string name) =>
		host.Bridge.LastEvent(session.Address, "sources", name);

	// The analysis runs in the session's background scope, so its result lands after the command returns.
	private static async Task<JsonElement> WaitForSourceAsync(TestHost host, HostSession session, string name) {
		for (int attempt = 0; attempt < 100; attempt++) {
			if (Source(host, session, name) is { } payload
				&& payload.GetProperty("target").GetString() == LearnTarget) {
				return payload;
			}

			await Task.Delay(50);
		}

		throw new InvalidOperationException($"The corrections tab never posted '{name}'.");
	}

	// Polls the ambient `suggestions` pushes for the corrections.learn card's presence/absence — the pushes
	// ride the corpus's Changed event, so the state settles asynchronously.
	private static async Task WaitForSuggestionAsync(TestHost host, bool present) {
		for (int attempt = 0; attempt < 50; attempt++) {
			var last = host.Bridge.LastEvent("suggestions", "changed");
			if (last is { } push && push.GetProperty("items").EnumerateArray()
				.Any(item => item.GetProperty("id").GetString() == "corrections.learn") == present) {
				return;
			}

			await Task.Delay(100);
		}

		throw new InvalidOperationException($"corrections.learn never became {(present ? "present" : "absent")}");
	}

	private sealed class LearnInferenceStub(InferenceResult<CorrectionLessonsOutput> result) : IInferenceService {
		public int Calls { get; private set; }

		public InferenceModelCategory Category { get; private set; }

		public InferenceInvocationOrigin Origin { get; private set; }

		public string? AgentProviderId { get; private set; }

		public string? Workspace { get; private set; }

		public string? Prompt { get; private set; }

		public Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
			InferenceOwner owner,
			InferenceModelCategory category,
			InferenceInput input,
			JsonTypeInfo<TResponse> responseType,
			InferenceQueryOptions options,
			CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			Assert.Same(CorrectionLessons.ResponseType, responseType);
			Assert.Equal(CorrectionLessons.QueryOptions, options);
			Assert.Empty(input.Images);
			Calls++;
			AgentProviderId = owner.AgentProviderId;
			Workspace = owner.Workspace;
			Category = category;
			Origin = options.Origin;
			Prompt = input.Prompt;
			return Task.FromResult((InferenceResult<TResponse>)(object)result);
		}
	}
}
