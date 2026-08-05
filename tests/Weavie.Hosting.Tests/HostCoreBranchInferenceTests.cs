using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCoreBranchInferenceTests {
	[Theory]
	[InlineData("claude")]
	[InlineData("codex")]
	public async Task Preview_UsesSelectedProviderAndValidatedUtilityProposalWithRepositoryContext(
		string agentProviderId) {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "bug/webm-fails-to-load" },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(repo => {
			TestHost.RunGit(repo, "branch", "bug/prior-failure");
			TestHost.RunGit(repo, "branch", "feature/mobile-inbox");
		}, _ => inference);

		string result = await PreviewAsync(host, "WebM files fail to load", agentProviderId);

		Assert.Equal("bug/webm-fails-to-load", result);
		Assert.Equal(agentProviderId, inference.AgentProviderId);
		Assert.Equal(InferenceModelCategory.Utility, inference.Category);
		Assert.Equal(InferenceInvocationOrigin.Automatic, inference.Origin);
		Assert.Contains("\"prompt\":\"WebM files fail to load\"", inference.Prompt, StringComparison.Ordinal);
		Assert.Contains("\"currentBranch\":\"main\"", inference.Prompt, StringComparison.Ordinal);
		Assert.Contains("bug/prior-failure", inference.Prompt, StringComparison.Ordinal);
		Assert.Contains("feature/mobile-inbox", inference.Prompt, StringComparison.Ordinal);
		Assert.Null(host.Core.SessionForTest("bug/webm-fails-to-load"));
	}

	[Theory]
	[InlineData(InferenceFailureKind.Disabled)]
	[InlineData(InferenceFailureKind.PolicyDenied)]
	[InlineData(InferenceFailureKind.NotConfigured)]
	[InlineData(InferenceFailureKind.CategoryUnavailable)]
	[InlineData(InferenceFailureKind.InputRejected)]
	[InlineData(InferenceFailureKind.TimedOut)]
	[InlineData(InferenceFailureKind.AuthenticationFailed)]
	[InlineData(InferenceFailureKind.RateLimited)]
	[InlineData(InferenceFailureKind.ProviderUnavailable)]
	[InlineData(InferenceFailureKind.Refused)]
	[InlineData(InferenceFailureKind.InvalidResponse)]
	public async Task EveryInferenceFailure_UsesTheSameDeterministicBranch(InferenceFailureKind kind) {
		var inference = new BranchInferenceStub(new InferenceFailure<BranchNameInferenceOutput> {
			Kind = kind,
			Detail = "failed",
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		string result = await PreviewAsync(host, "WebM files fail to load", "claude");

		Assert.Equal("webm-files-fail-to-load", result);
		Assert.Equal(1, inference.Calls);
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("not a valid branch")]
	[InlineData("main")]
	[InlineData("HEAD")]
	[InlineData("foo/.bar")]
	[InlineData("foo.lock/bar")]
	public async Task InvalidOrCollidingProposal_UsesDeterministicBranch(string proposed) {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = proposed },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		string result = await PreviewAsync(host, "Fix WebM", "claude");

		Assert.Equal("fix-webm", result);
	}

	[Fact]
	public async Task DeterministicPreview_AvoidsLocalBranchWithoutAWeavieSession() {
		var inference = new BranchInferenceStub(new InferenceFailure<BranchNameInferenceOutput> {
			Kind = InferenceFailureKind.Disabled,
			Detail = "disabled",
		});
		await using var host = await TestHost.StartAsync(
			repo => TestHost.RunGit(repo, "branch", "fix-webm"),
			_ => inference);

		string result = await PreviewAsync(host, "Fix WebM", "claude");

		Assert.Equal("fix-webm-2", result);
	}

	[Fact]
	public async Task OmittedBranch_UsesDeterministicNameWithoutInference() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "should-not-run" },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Prompt = "Fix WebM",
			Base = "main",
		});

		Assert.True(result.Ok, result.Error);
		Assert.Equal(0, inference.Calls);
		Assert.Equal("fix-webm", host.Session("fix-webm").SlotId);
	}

	[Fact]
	public async Task CancelledPreview_CancelsInferenceWithoutCreatingAWorktree() {
		var inference = new CancellableInferenceStub();
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);
		const string requestId = "branch-preview";
		var peer = new WebPeer(TestHost.TestPageId);

		host.Bridge.Receive(
			peer,
			MessageEnvelope.SessionRequest(
				host.PrimarySession.Address,
				requestId,
				"sessionCreation",
				"previewBranch",
				JsonSerializer.SerializeToElement(new {
					prompt = "Fix WebM",
					agentProviderId = "codex",
				})).ToJson());
		await inference.Started.Task;

		host.Bridge.Receive(
			peer,
			MessageEnvelope.SessionCancel(
				host.PrimarySession.Address,
				requestId,
				"sessionCreation",
				"previewBranch").ToJson());
		await inference.Cancelled.Task;

		Assert.Null(host.Core.SessionForTest("fix-webm"));
	}

	private static async Task<string> PreviewAsync(TestHost host, string prompt, string agentProviderId) {
		var result = await host.SessionRequestAsync<JsonElement>(
			host.PrimarySession,
			"sessionCreation",
			"previewBranch",
			new { prompt, agentProviderId });
		return result.GetProperty("branch").GetString()!;
	}

	private static InferenceReceipt Receipt() => new() {
		ProviderId = "test",
		Category = InferenceModelCategory.Utility,
		ModelId = "utility-model",
		Duration = TimeSpan.Zero,
	};

	private sealed class BranchInferenceStub(InferenceResult<BranchNameInferenceOutput> result) : IInferenceService {
		public int Calls { get; private set; }

		public InferenceModelCategory Category { get; private set; }

		public string? AgentProviderId { get; private set; }

		public InferenceInvocationOrigin Origin { get; private set; }

		public string? Prompt { get; private set; }

		public Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
			string agentProviderId,
			InferenceModelCategory category,
			string prompt,
			JsonTypeInfo<TResponse> responseType,
			InferenceQueryOptions options,
			CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			Assert.Same(BranchNameInference.ResponseType, responseType);
			Assert.Same(BranchNameInference.QueryOptions, options);
			Calls++;
			AgentProviderId = agentProviderId;
			Category = category;
			Origin = options.Origin;
			Prompt = prompt;
			return Task.FromResult((InferenceResult<TResponse>)(object)result);
		}
	}

	private sealed class CancellableInferenceStub : IInferenceService {
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
			string agentProviderId,
			InferenceModelCategory category,
			string prompt,
			JsonTypeInfo<TResponse> responseType,
			InferenceQueryOptions options,
			CancellationToken ct) {
			using var registration = ct.Register(Cancelled.SetResult);
			Started.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			throw new InvalidOperationException("Infinite inference unexpectedly completed.");
		}
	}
}
