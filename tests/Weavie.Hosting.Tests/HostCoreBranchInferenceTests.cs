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

		var result = await PreviewAsync(host, "WebM files fail to load", agentProviderId);

		Assert.Equal("bug/webm-fails-to-load", result.Branch);
		Assert.False(result.InferenceFailed);
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
	public async Task EveryInferenceFailure_LeavesTheBranchBlank(InferenceFailureKind kind) {
		var inference = new BranchInferenceStub(new InferenceFailure<BranchNameInferenceOutput> {
			Kind = kind,
			Detail = "failed",
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await PreviewAsync(host, "WebM files fail to load", "claude");

		Assert.Empty(result.Branch);
		Assert.True(result.InferenceFailed);
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
	public async Task InvalidOrCollidingProposal_LeavesTheBranchBlank(string proposed) {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = proposed },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await PreviewAsync(host, "Fix WebM", "claude");

		Assert.Empty(result.Branch);
		Assert.True(result.InferenceFailed);
	}

	[Fact]
	public async Task OmittedBranch_IsRejectedWithoutInference() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "should-not-run" },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Prompt = "Fix WebM",
			Base = "main",
		});

		Assert.False(result.Ok);
		Assert.Contains("Type a branch name", result.Error, StringComparison.Ordinal);
		Assert.Equal(0, inference.Calls);
		Assert.Null(host.Core.SessionForTest("fix-webm"));
	}

	[Fact]
	public async Task MissingSource_DoesNotFallBackToTheWorkspaceCheckout() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "should-not-run" },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await host.HostRequestAsync<JsonElement>(
			"sessionCreation",
			"previewBranch",
			new { sourceId = "missing", prompt = "Fix WebM", agentProviderId = "claude" });

		Assert.Equal(string.Empty, result.GetProperty("branch").GetString());
		Assert.True(result.GetProperty("inferenceFailed").GetBoolean());
		Assert.Equal(0, inference.Calls);
	}

	[Fact]
	public async Task CancelledPreview_CancelsInferenceWithoutCreatingAWorktree() {
		var inference = new CancellableInferenceStub();
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);
		const string requestId = "branch-preview";
		var peer = new WebPeer(TestHost.TestPageId);

		host.Bridge.Receive(
			peer,
			MessageEnvelope.Request(
				MessageScope.Host,
				null,
				requestId,
				"sessionCreation",
				"previewBranch",
				JsonSerializer.SerializeToElement(new {
					sourceId = host.WorkspaceSession.SlotId,
					prompt = "Fix WebM",
					agentProviderId = "codex",
				})).ToJson());
		await inference.Started.Task;

		host.Bridge.Receive(
			peer,
			MessageEnvelope.Cancel(
				MessageScope.Host,
				null,
				requestId,
				"sessionCreation",
				"previewBranch").ToJson());
		await inference.Cancelled.Task;

		Assert.Null(host.Core.SessionForTest("fix-webm"));
	}

	private static async Task<BranchPreview> PreviewAsync(TestHost host, string prompt, string agentProviderId) {
		var result = await host.HostRequestAsync<JsonElement>(
			"sessionCreation",
			"previewBranch",
			new { sourceId = host.WorkspaceSession.SlotId, prompt, agentProviderId });
		return new BranchPreview(
			result.GetProperty("branch").GetString()!,
			result.GetProperty("inferenceFailed").GetBoolean());
	}

	private sealed record BranchPreview(string Branch, bool InferenceFailed);

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
