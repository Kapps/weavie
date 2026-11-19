using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class SettingOwnershipTests {
	[Fact]
	public void ResetsOnlyNameKeysOfTheSameOwner() {
		var registry = CoreSettings.CreateRegistry();

		Assert.All(
			registry.Definitions,
			definition => Assert.All(definition.Resets, key => Assert.Equal(definition.Owner, registry.Require(key).Owner)));
	}
}
