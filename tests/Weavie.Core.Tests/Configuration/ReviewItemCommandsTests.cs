using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The review item toggles flip their settings, so the choice survives a restart.</summary>
[Collection("Settings")]
public sealed class ReviewItemCommandsTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-review-item-tests");

	public void Dispose() => _dir.Dispose();

	[Theory]
	[InlineData(CoreCommands.PullRequestToggleComments, EditorSettings.ReviewComments, true)]
	[InlineData(CoreCommands.ReviewHintsToggle, EditorSettings.ReviewHints, true)]
	[InlineData(CoreCommands.ReviewToggleSettledItems, EditorSettings.ReviewStepSettled, false)]
	public async Task Toggle_FlipsItsSettingBothWays(string command, string key, bool initial) {
		string file = _dir.Combine("settings.toml");
		using var store = CoreSettings.CreateStore(file, enableWatcher: false);
		var commands = new CommandDispatcher(CoreCommands.CreateRegistry());
		ReviewItemCommands.RegisterHandlers(commands, store);
		Assert.Equal(initial, store.RequireBool(key));

		Assert.True((await commands.InvokeAsync(command, null, CancellationToken.None)).Ok);
		Assert.Equal(!initial, store.RequireBool(key));
		Assert.Contains(key, File.ReadAllText(file), StringComparison.Ordinal);

		Assert.True((await commands.InvokeAsync(command, null, CancellationToken.None)).Ok);
		Assert.Equal(initial, store.RequireBool(key));
	}
}
