using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCoreBranchInferenceTests {
	[Fact]
	public async Task Preview_UsesOwningWorkspaceAndValidatedUtilityProposalWithRepositoryContext() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "bug/webm-fails-to-load", NeedsMoreDetail = false },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(repo => {
			TempGitRepo.Run(repo, "branch", "bug/prior-failure");
			TempGitRepo.Run(repo, "branch", "feature/mobile-inbox");
		}, _ => inference);

		var result = await PreviewAsync(host, "WebM files fail to load");

		Assert.Equal("bug/webm-fails-to-load", result.Branch);
		Assert.Null(result.Error);
		Assert.Equal(host.RepoRoot, inference.Workspace);
		Assert.Equal(InferenceModelCategory.Utility, inference.Category);
		Assert.Equal(InferenceInvocationOrigin.Automatic, inference.Origin);
		Assert.Equal(TimeSpan.FromSeconds(24), BranchNameInference.QueryOptions.TimeBudget);
		Assert.Contains("\"prompt\":\"WebM files fail to load\"", inference.Prompt, StringComparison.Ordinal);
		Assert.Contains("\"currentBranch\":\"main\"", inference.Prompt, StringComparison.Ordinal);
		Assert.Contains("bug/prior-failure", inference.Prompt, StringComparison.Ordinal);
		Assert.Contains("feature/mobile-inbox", inference.Prompt, StringComparison.Ordinal);
		Assert.Null(host.Core.SessionForTest("bug/webm-fails-to-load"));
	}

	[Fact]
	public async Task Preview_LearnsFromTheUsersOwnBranchesInsteadOfOtherAuthors() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "kapps/webm-fails-to-load", NeedsMoreDetail = false },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(repo => {
			TempGitRepo.Run(repo, "checkout", "--quiet", "-b", "teammate/inbox-polish");
			TempGitRepo.Run(repo, "-c", "user.email=teammate@example.com", "-c", "user.name=Teammate",
				"commit", "--quiet", "--allow-empty", "-m", "theirs");
			TempGitRepo.Run(repo, "checkout", "--quiet", "-b", "kapps/prior-fix", "main");
			TempGitRepo.Run(repo, "commit", "--quiet", "--allow-empty", "-m", "mine");
			TempGitRepo.Run(repo, "checkout", "--quiet", "main");
		}, _ => inference);

		var result = await PreviewAsync(host, "WebM files fail to load");

		Assert.Equal("kapps/webm-fails-to-load", result.Branch);
		var input = InputJson(inference.Prompt!);
		Assert.Equal(TempGitRepo.AuthorEmail, input.GetProperty("authorEmail").GetString());
		string[] mine = Branches(input, "myRecentBranches");
		Assert.Contains("kapps/prior-fix", mine);
		Assert.DoesNotContain("main", mine);
		Assert.DoesNotContain("teammate/inbox-polish", mine);
		Assert.Empty(Branches(input, "otherRecentBranches"));
	}

	[Fact]
	public async Task Preview_ReadsOtherAuthorsBranchesWhenTheUserAuthoredOnlyTheDefaultBranch() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "kapps/webm-fails", NeedsMoreDetail = false },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(repo => {
			TempGitRepo.Run(repo, "checkout", "--quiet", "-b", "teammate/inbox-polish");
			TempGitRepo.Run(repo, "-c", "user.email=teammate@example.com", "-c", "user.name=Teammate",
				"commit", "--quiet", "--allow-empty", "-m", "theirs");
			TempGitRepo.Run(repo, "checkout", "--quiet", "main");
		}, _ => inference);

		var result = await PreviewAsync(host, "WebM files fail to load");

		Assert.Equal("kapps/webm-fails", result.Branch);
		var input = InputJson(inference.Prompt!);
		Assert.Equal(TempGitRepo.AuthorEmail, input.GetProperty("authorEmail").GetString());
		Assert.Empty(Branches(input, "myRecentBranches"));
		Assert.Equal(["teammate/inbox-polish"], Branches(input, "otherRecentBranches"));
	}

	[Fact]
	public async Task ImageOnlyPreview_PassesTheExactDecodedImageToInference() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "bug/screenshot-layout", NeedsMoreDetail = false },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await PreviewWithAttachmentsAsync(
			host,
			string.Empty,
			[new NewSessionAttachment { Id = "image-1", Mime = "image/png", DataB64 = "AQIDBA==" }]);

		Assert.Equal("bug/screenshot-layout", result.Branch);
		Assert.Contains("\"prompt\":\"\"", inference.Prompt, StringComparison.Ordinal);
		var image = Assert.Single(inference.Images!);
		Assert.Equal("image/png", image.Mime);
		Assert.Equal([1, 2, 3, 4], image.Bytes.ToArray());
	}

	[Fact]
	public async Task InvalidImage_FailsBeforeInference() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "should-not-run", NeedsMoreDetail = false },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await PreviewWithAttachmentsAsync(
			host,
			string.Empty,
			[new NewSessionAttachment { Id = "image-1", Mime = "text/plain", DataB64 = "AQ==" }]);

		Assert.Empty(result.Branch);
		Assert.Contains("image type", result.Error, StringComparison.Ordinal);
		Assert.Equal(0, inference.Calls);
	}

	[Fact]
	public async Task TooManyImages_FailBeforeInferenceOrDecoding() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "should-not-run", NeedsMoreDetail = false },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);
		var attachments = Enumerable.Range(1, BranchNameInference.QueryOptions.MaxImageCount + 1)
			.Select(index => new NewSessionAttachment {
				Id = $"image-{index}",
				Mime = "image/png",
				DataB64 = "AQ==",
			})
			.ToArray();

		var result = await PreviewWithAttachmentsAsync(host, string.Empty, attachments);

		Assert.Empty(result.Branch);
		Assert.Contains("up to", result.Error, StringComparison.Ordinal);
		Assert.Equal(0, inference.Calls);
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
	public async Task EveryInferenceFailure_ReportsItsReason(InferenceFailureKind kind) {
		var inference = new BranchInferenceStub(new InferenceFailure<BranchNameInferenceOutput> {
			Kind = kind,
			Detail = "The selected provider failed.",
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await PreviewAsync(host, "WebM files fail to load");

		Assert.Empty(result.Branch);
		Assert.Equal("The selected provider failed.", result.Error);
		Assert.Equal(1, inference.Calls);
	}

	[Fact]
	public async Task Resuggesting_IsUserInitiatedSoTheAutomaticPolicyDoesNotGateIt() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "bug/asked-again", NeedsMoreDetail = false },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await ResuggestAsync(host, "WebM files fail to load");

		Assert.Equal("bug/asked-again", result.Branch);
		Assert.Equal(InferenceInvocationOrigin.UserInitiated, inference.Origin);
	}

	[Fact]
	public async Task VagueProposal_AsksForMoreDetailWithoutAnError() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = string.Empty, NeedsMoreDetail = true },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await PreviewAsync(host, "fix the bug");

		Assert.Empty(result.Branch);
		Assert.Null(result.Error);
		Assert.True(result.NeedsMoreDetail);
	}

	[Theory]
	[InlineData("", "The inference provider returned an empty branch name.")]
	[InlineData(" ", "The inference provider returned an empty branch name.")]
	[InlineData("not a valid branch", "The suggested branch name isn't valid.")]
	[InlineData("main", "The suggested branch name is already in use.")]
	[InlineData("HEAD", "The suggested branch name isn't valid.")]
	[InlineData("foo/.bar", "The suggested branch name isn't valid.")]
	[InlineData("foo.lock/bar", "The suggested branch name isn't valid.")]
	public async Task InvalidOrCollidingProposal_ReportsItsReason(string proposed, string error) {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = proposed, NeedsMoreDetail = false },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await PreviewAsync(host, "Fix WebM");

		Assert.Empty(result.Branch);
		Assert.Equal(error, result.Error);
	}

	[Fact]
	public async Task OmittedBranch_IsRejectedWithoutInference() {
		var inference = new BranchInferenceStub(new InferenceSuccess<BranchNameInferenceOutput> {
			Value = new BranchNameInferenceOutput { Branch = "should-not-run", NeedsMoreDetail = false },
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
			Value = new BranchNameInferenceOutput { Branch = "should-not-run", NeedsMoreDetail = false },
			Receipt = Receipt(),
		});
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		var result = await host.HostRequestAsync<JsonElement>(
			"sessionCreation",
			"previewBranch",
			new {
				sourceId = "missing",
				prompt = "Fix WebM",
				attachments = Array.Empty<object>(),
			});

		Assert.Equal(string.Empty, result.GetProperty("branch").GetString());
		Assert.Equal("The source session no longer exists.", result.GetProperty("error").GetString());
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
					attachments = Array.Empty<object>(),
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

	private static JsonElement InputJson(string prompt) {
		const string marker = "Input JSON:\n";
		int start = prompt.IndexOf(marker, StringComparison.Ordinal);
		Assert.InRange(start, 0, prompt.Length);
		return JsonDocument.Parse(prompt[(start + marker.Length)..]).RootElement.Clone();
	}

	private static string[] Branches(JsonElement input, string field) =>
		[.. input.GetProperty(field).EnumerateArray().Select(branch => branch.GetString()!)];

	private static Task<BranchPreview> PreviewAsync(TestHost host, string prompt) =>
		PreviewWithAttachmentsAsync(host, prompt, []);

	private static Task<BranchPreview> ResuggestAsync(TestHost host, string prompt) =>
		RequestPreviewAsync(host, prompt, [], userInitiated: true);

	private static Task<BranchPreview> PreviewWithAttachmentsAsync(
		TestHost host,
		string prompt,
		IReadOnlyList<NewSessionAttachment> attachments) =>
		RequestPreviewAsync(host, prompt, attachments, userInitiated: false);

	private static async Task<BranchPreview> RequestPreviewAsync(
		TestHost host,
		string prompt,
		IReadOnlyList<NewSessionAttachment> attachments,
		bool userInitiated) {
		var result = await host.HostRequestAsync<JsonElement>(
			"sessionCreation",
			"previewBranch",
			new {
				sourceId = host.WorkspaceSession.SlotId,
				prompt,
				attachments,
				userInitiated,
			});
		return new BranchPreview(
			result.GetProperty("branch").GetString()!,
			result.GetProperty("error").ValueKind == JsonValueKind.Null
				? null
				: result.GetProperty("error").GetString(),
			result.GetProperty("needsMoreDetail").GetBoolean());
	}

	private sealed record BranchPreview(string Branch, string? Error, bool NeedsMoreDetail);

	private static InferenceReceipt Receipt() => new() {
		ProviderId = "test",
		Category = InferenceModelCategory.Utility,
		ModelId = "utility-model",
		Duration = TimeSpan.Zero,
	};

	private sealed class BranchInferenceStub(InferenceResult<BranchNameInferenceOutput> result) : IInferenceService {
		public int Calls { get; private set; }

		public InferenceModelCategory Category { get; private set; }

		public string? Workspace { get; private set; }

		public InferenceInvocationOrigin Origin { get; private set; }

		public string? Prompt { get; private set; }

		public IReadOnlyList<InferenceInputImage>? Images { get; private set; }

		public Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
			InferenceOwner owner,
			InferenceModelCategory category,
			InferenceInput input,
			JsonTypeInfo<TResponse> responseType,
			InferenceQueryOptions options,
			CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			Assert.Same(BranchNameInference.ResponseType, responseType);
			// The origin varies with who asked; every declared bound must still be the feature's own.
			Assert.Equal(BranchNameInference.QueryOptions with { Origin = options.Origin }, options);
			Calls++;
			Workspace = owner.Workspace;
			Category = category;
			Origin = options.Origin;
			Prompt = input.Prompt;
			Images = input.Images;
			return Task.FromResult((InferenceResult<TResponse>)(object)result);
		}
	}

	private sealed class CancellableInferenceStub : IInferenceService {
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
			InferenceOwner owner,
			InferenceModelCategory category,
			InferenceInput input,
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
