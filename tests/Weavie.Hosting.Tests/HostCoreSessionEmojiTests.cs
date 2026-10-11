using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSessionEmojiTests {
	[Fact]
	public async Task DisabledInference_KeepsTheMonogramChipWithoutQuerying() {
		var inference = new EmojiInferenceStub("🔍");
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		Assert.Equal(0, inference.Calls);
		Assert.Null(Emoji(WorkspaceEntry(host)));
	}

	[Fact]
	public async Task EnablingAutomaticInference_PicksAndPublishesTheBranchEmoji() {
		var inference = new EmojiInferenceStub("🔍");
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		EnableAutomaticInference(host);
		await Wait.UntilAsync(() => Emoji(WorkspaceEntry(host)) == "🔍");

		Assert.Equal(1, inference.Calls);
		Assert.Equal(host.RepoRoot, inference.Workspace);
		Assert.Contains("\"branch\":\"main\"", inference.Prompt, StringComparison.Ordinal);
	}

	[Fact]
	public async Task NonEmojiReply_WarnsOnceAndKeepsTheMonogram() {
		var inference = new EmojiInferenceStub("AB");
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);

		EnableAutomaticInference(host);
		await Wait.UntilAsync(() => Warning(host) is not null);

		Assert.Contains("isn't one emoji", Warning(host), StringComparison.Ordinal);
		Assert.Equal(1, inference.Calls);
		Assert.Null(Emoji(WorkspaceEntry(host)));
	}

	[Fact]
	public async Task SavedEmojis_SurviveRestartWithoutQueryingAgain() {
		var inference = new EmojiInferenceStub("🔍");
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);
		EnableAutomaticInference(host);
		Assert.True((await host.CreateSessionAsync("feat-zoom")).Ok);
		await Wait.UntilAsync(() => Catalog(host).All(entry => Emoji(entry) == "🔍"));
		int calls = inference.Calls;

		await host.RestartAsync();

		Assert.All(Catalog(host), entry => Assert.Equal("🔍", Emoji(entry)));
		Assert.Equal(2, calls);
		Assert.Equal(calls, inference.Calls);
	}

	[Fact]
	public async Task WorkspaceCheckoutOnAnotherBranch_PicksAFreshEmoji() {
		var inference = new EmojiInferenceStub("🔍");
		await using var host = await TestHost.StartAsync(_ => { }, _ => inference);
		EnableAutomaticInference(host);
		await Wait.UntilAsync(() => Emoji(WorkspaceEntry(host)) == "🔍");

		inference.Emoji = "🧪";
		await host.RestartAsync(() => TempGitRepo.Run(host.RepoRoot, "checkout", "--quiet", "-b", "test/other"));

		await Wait.UntilAsync(() => Emoji(WorkspaceEntry(host)) == "🧪");
		Assert.Contains("\"branch\":\"test/other\"", inference.Prompt, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("🔍", true)]
	[InlineData("🏷️", true)]
	[InlineData("", false)]
	[InlineData("A", false)]
	[InlineData("©", false)]
	[InlineData("🔍🔍", false)]
	public void IsSingleEmoji_AcceptsExactlyOnePictograph(string reply, bool expected) =>
		Assert.Equal(expected, SessionEmojiInference.IsSingleEmoji(reply));

	private static void EnableAutomaticInference(TestHost host) {
		host.Settings.Set(InferenceSettings.AllowAutomatic, JsonSerializer.SerializeToElement(true));
		host.Settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));
	}

	private static JsonElement[] Catalog(TestHost host) =>
		[.. host.Bridge.LastEvent("sessions", "catalog")!.Value.EnumerateArray()];

	private static JsonElement WorkspaceEntry(TestHost host) =>
		Catalog(host).Single(entry => entry.GetProperty("workspaceCheckout").GetBoolean());

	private static string? Emoji(JsonElement entry) => entry.GetProperty("emoji").GetString();

	private static string? Warning(TestHost host) =>
		host.Bridge.PostedEvents("notifications", "show")
			.Where(message => message.TryGetProperty("key", out var key) && key.GetString() == "session-emoji")
			.Select(message => message.GetProperty("message").GetString())
			.SingleOrDefault();

	private sealed class EmojiInferenceStub(string emoji) : IInferenceService {
		public string Emoji { get; set; } = emoji;

		public int Calls { get; private set; }

		public string? Workspace { get; private set; }

		public string? Prompt { get; private set; }

		public Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
			InferenceOwner owner,
			InferenceModelCategory category,
			InferenceInput input,
			JsonTypeInfo<TResponse> responseType,
			InferenceQueryOptions options,
			CancellationToken ct) {
			Assert.Same(SessionEmojiInference.ResponseType, responseType);
			Assert.Equal(SessionEmojiInference.QueryOptions, options);
			Calls++;
			Workspace = owner.Workspace;
			Prompt = input.Prompt;
			InferenceResult<SessionEmojiInferenceOutput> result = new InferenceSuccess<SessionEmojiInferenceOutput> {
				Value = new SessionEmojiInferenceOutput { Emoji = Emoji },
				Receipt = new InferenceReceipt {
					ProviderId = "test",
					Category = category,
					ModelId = "utility-model",
					Duration = TimeSpan.Zero,
				},
			};
			return Task.FromResult((InferenceResult<TResponse>)(object)result);
		}
	}
}
