using Weavie.Core.Commands;
using Weavie.Core.Tips;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class StartupTipsTests {
	[Fact]
	public void Catalog_IsCuratedAndReferencesRegisteredCommands() {
		var tips = StartupTips.All;
		var commands = CoreCommands.CreateRegistry();

		Assert.Equal(10, tips.Count);
		Assert.Equal(tips.Count, tips.Select(tip => tip.Id).Distinct(StringComparer.Ordinal).Count());
		Assert.All(tips, tip => {
			Assert.False(string.IsNullOrWhiteSpace(tip.Id));
			Assert.False(string.IsNullOrWhiteSpace(tip.Lead));
			Assert.False(string.IsNullOrWhiteSpace(tip.Detail));
			if (tip.CommandId is { } commandId) {
				Assert.True(commands.TryGet(commandId, out _), $"Tip '{tip.Id}' references unknown command '{commandId}'.");
			}
		});
	}

	[Fact]
	public void Pick_ReturnsACatalogMember() {
		var tip = StartupTips.Pick(new Random(42));

		Assert.Contains(tip, StartupTips.All);
	}
}
