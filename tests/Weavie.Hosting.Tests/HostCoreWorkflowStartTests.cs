using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.Hosting.Messaging;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

// The new-session composer's workflow requests: what it offers, and what a prompt suggests.
public sealed class HostCoreWorkflowStartTests {
	private static async Task<TestHost> StartAsync(IInferenceService inference) {
		var host = await TestHost.StartAsync(repo => {
			Directory.CreateDirectory(Path.Combine(repo, ".weavie", "workflows"));
			File.WriteAllText(Path.Combine(repo, ".weavie", "workflows", "typed.yaml"),
				"id: typed\ndescription: Needs a url.\ninputs: { link: url }\nstages:\n  - id: a\n    run: { prompt: \"{{inputs.link}}\" }\n");
			File.WriteAllText(Path.Combine(repo, ".weavie", "workflows", "broken.yaml"), "id: broken\ndescription: d\nstages:\n  - id: a\n    run: {}\n");
			CommitFlow(repo);
		}, _ => inference);
		host.Settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));
		host.Settings.Set(InferenceSettings.AllowAutomatic, JsonSerializer.SerializeToElement(true));
		return host;
	}

	private static Task<JsonElement> CatalogAsync(TestHost host, string? sourceId) =>
		host.HostRequestAsync<JsonElement>("sessionCreation", "workflows", new { sourceId, providerId = "structured" });

	private static async Task<(bool Applies, string? Workflow, string? Error)> SuggestAsync(TestHost host) {
		var result = await host.HostRequestAsync<JsonElement>(
			"sessionCreation", "suggestWorkflow", new { sourceId = (string?)null, providerId = "structured", prompt = "tidy the draft and polish it" });
		return (result.GetProperty("applies").GetBoolean(), result.GetProperty("workflow").GetString(), result.GetProperty("error").GetString());
	}

	private static string[] Ids(JsonElement catalog) =>
		[.. catalog.GetProperty("workflows").EnumerateArray().Select(workflow => workflow.GetProperty("id").GetString()!)];

	[Fact]
	public async Task TheCatalogOffersWhatAPromptCanStartFromWhereTheSessionStarts() {
		await using var host = await StartAsync(new DecisionStub("flow", 0.9));

		var catalog = await CatalogAsync(host, null);
		Assert.Equal(["broken", "flow", "quick-fix", "spec-driven"], Ids(catalog));
		var workflows = catalog.GetProperty("workflows");
		Assert.Contains("run needs exactly one of prompt or skill", workflows[0].GetProperty("error").GetString(), StringComparison.Ordinal);
		Assert.Equal(("a stage the user approves, then a stage the model decides on", JsonValueKind.Null),
			(workflows[1].GetProperty("description").GetString(), workflows[1].GetProperty("error").ValueKind));
		Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (catalog.GetProperty("default").ValueKind, catalog.GetProperty("defaultProblem").ValueKind));

		host.Settings.Set(WorkflowSettings.Default, JsonSerializer.SerializeToElement("flow"));
		Assert.Equal("flow", (await CatalogAsync(host, null)).GetProperty("default").GetString());

		// A session's own uncommitted workflow is offered only when the start is made from that session.
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		File.WriteAllText(Path.Combine(host.Session("wf").WorkspaceRoot, ".weavie", "workflows", "local.yaml"),
			"id: local\ndescription: d\nstages:\n  - id: a\n    run: { prompt: \"x\" }\n");
		Assert.Contains("local", Ids(await CatalogAsync(host, "wf")));
		Assert.DoesNotContain("local", Ids(await CatalogAsync(host, null)));
		await Assert.ThrowsAnyAsync<Exception>(() => CatalogAsync(host, "gone"));
	}

	[Fact]
	public async Task ASuggestionReportsTheConfidentChoiceOrWhyTheDecisionFailed() {
		var inference = new SwitchableInference(new DecisionStub("flow", 0.9));
		await using var host = await StartAsync(inference);

		Assert.Equal((true, "flow", null), await SuggestAsync(host));
		inference.Failing = true;
		Assert.Equal((false, null, "the model is down"), await SuggestAsync(host));
	}

	// The test host drains every request before returning, so a held one is sent on the bridge directly.
	[Fact]
	public async Task ASuggestionAnswersWhileABranchPreviewIsStillRunning() {
		var inference = new HeldBranchNaming(new DecisionStub("flow", 0.9));
		await using var host = await StartAsync(inference);
		var peer = new WebPeer(TestHost.TestPageId);
		string Request(string id, string name, object payload) => MessageEnvelope.Request(
			MessageScope.Host, null, id, "sessionCreation", name, JsonSerializer.SerializeToElement(payload)).ToJson();

		host.Bridge.Receive(peer, Request("held-preview", "previewBranch",
			new { sourceId = (string?)null, prompt = "tidy the draft", attachments = Array.Empty<object>(), userInitiated = false }));
		await inference.BranchAsked.Task.WaitAsync(TimeSpan.FromSeconds(10));
		host.Bridge.Receive(peer, Request("suggestion", "suggestWorkflow", new { sourceId = (string?)null, providerId = "structured", prompt = "tidy the draft" }));

		MessageEnvelope? Response(string id) => host.Bridge.Posted
			.Select(json => MessageEnvelope.TryParse(json, out var envelope) ? envelope : null)
			.FirstOrDefault(envelope => envelope is { Kind: MessageKind.Response } && envelope.RequestId == id);
		await Eventually(() => Response("suggestion") is not null);
		Assert.Equal("flow", Response("suggestion")!.Payload.GetProperty("workflow").GetString());
		Assert.Null(Response("held-preview"));

		host.Bridge.Receive(peer, MessageEnvelope.Cancel(MessageScope.Host, null, "held-preview", "sessionCreation", "previewBranch").ToJson());
		await inference.BranchCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}

	private sealed class SwitchableInference(DecisionStub decisions) : IInferenceService {
		public bool Failing { get; set; }

		public Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
			InferenceOwner owner, InferenceModelCategory category, InferenceInput input, InferenceResponseType<TResponse> responseType,
			InferenceQueryOptions options, CancellationToken ct) => !Failing
			? decisions.QueryAsync(owner, category, input, responseType, options, ct)
			: Task.FromResult<InferenceResult<TResponse>>(new InferenceFailure<TResponse> {
				Kind = InferenceFailureKind.ProviderUnavailable,
				Detail = "the model is down",
				Receipt = null,
			});
	}

	// Holds every branch-name query open until it is cancelled, and answers decisions at once.
	private sealed class HeldBranchNaming(DecisionStub decisions) : IInferenceService {
		public TaskCompletionSource BranchAsked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource BranchCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
			InferenceOwner owner, InferenceModelCategory category, InferenceInput input, InferenceResponseType<TResponse> responseType,
			InferenceQueryOptions options, CancellationToken ct) {
			if (typeof(TResponse) != typeof(BranchNameInferenceOutput)) {
				return await decisions.QueryAsync(owner, category, input, responseType, options, ct);
			}

			BranchAsked.TrySetResult();
			try {
				await Task.Delay(Timeout.Infinite, ct);
			} finally {
				BranchCancelled.TrySetResult();
			}

			throw new OperationCanceledException(ct);
		}
	}
}
