using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The <c>pr.autoReviewPrompt</c> setting that gates seeding Claude's first message when a PR is opened:
/// it must be a Bool defaulting to on, so opening a PR keeps its current "help me review" behavior unless
/// the user turns it off.
/// </summary>
public sealed class PrSettingsTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-pr-settings-tests");

	public void Dispose() => _dir.Dispose();

	private string FilePath => _dir.Combine("settings.toml");

	[Fact]
	public void AutoReviewPrompt_IsBoolDefaultingOn() {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		var resolved = store.Resolve(CoreSettings.PullRequestAutoReviewPrompt);
		Assert.Equal(true, resolved.Value);
		Assert.True(store.RequireBool(CoreSettings.PullRequestAutoReviewPrompt));
	}

	[Fact]
	public void AutoReviewPrompt_HonoursUserOverride() {
		File.WriteAllText(FilePath, "pr.autoReviewPrompt = false\n");
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		Assert.False(store.RequireBool(CoreSettings.PullRequestAutoReviewPrompt));
	}
}
