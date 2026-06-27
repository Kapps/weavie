using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The <c>pr.autoReviewPrompt</c> setting that gates seeding Claude's first message when a PR is opened:
/// it must be a Bool defaulting to on, so opening a PR keeps its current "help me review" behavior unless
/// the user turns it off.
/// </summary>
public sealed class PrSettingsTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-pr-settings-tests", Guid.NewGuid().ToString("N"));

	public PrSettingsTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private string FilePath => Path.Combine(_dir, "settings.toml");

	[Fact]
	public void AutoReviewPrompt_IsBoolDefaultingOn() {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		var resolved = store.Resolve("pr.autoReviewPrompt");
		Assert.Equal(true, resolved.Value);
		Assert.True(store.RequireBool("pr.autoReviewPrompt"));
	}

	[Fact]
	public void AutoReviewPrompt_HonoursUserOverride() {
		File.WriteAllText(FilePath, "pr.autoReviewPrompt = false\n");
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		Assert.False(store.GetBool("pr.autoReviewPrompt", fallback: true));
	}
}
