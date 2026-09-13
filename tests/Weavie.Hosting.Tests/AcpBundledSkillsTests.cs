using System.Text.Json;
using Weavie.Core.Skills;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpBundledSkillsTests {
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SkillDiscoveryReachesProvidersWithAndWithoutEmbeddedContext(bool embeddedContext) {
		await using var fixture = AcpAgentSessionFixture.CreateWithEmbeddedContext(embeddedContext);
		await fixture.StartAsync();
		fixture.Submit("help me report a Weavie bug");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		fixture.Submit("continue");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");

		var requests = AcpPromptAssertions.Read(fixture);
		var guidance = Assert.Single(AcpPromptAssertions.Blocks(requests[0]), IsGuidance);
		Assert.Equal(embeddedContext ? "resource" : "text", guidance.GetProperty("type").GetString());
		string text = Text(guidance);
		Assert.Contains(BundledSkills.Catalog(), text, StringComparison.Ordinal);
		Assert.DoesNotContain("Never include source code", text, StringComparison.Ordinal);
		Assert.DoesNotContain(AcpPromptAssertions.Blocks(requests[1]), IsGuidance);
		Assert.Equal(["help me report a Weavie bug", "continue"], fixture.Messages
			.Where(message => message.Type == "user-message").Select(message => message.Text));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ReopenedProviderSessionReceivesCurrentSkillDiscovery(bool embeddedContext) {
		await using var fixture = AcpAgentSessionFixture.CreateWithEmbeddedContext(embeddedContext);
		await fixture.StartAsync();
		fixture.Submit("first turn");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		fixture.Session.Restart();
		await fixture.WaitForControlsAsync(state => state.Ready);
		fixture.Submit("continue after reopening");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");

		foreach (var request in AcpPromptAssertions.Read(fixture)) {
			var guidance = Assert.Single(AcpPromptAssertions.Blocks(request), IsGuidance);
			Assert.Contains(BundledSkills.Catalog(), Text(guidance), StringComparison.Ordinal);
		}
	}

	private static bool IsGuidance(JsonElement block) =>
		Text(block).Contains("## Weavie-only skills", StringComparison.Ordinal);

	private static string Text(JsonElement block) =>
		block.GetProperty("type").GetString() == "resource"
			? block.GetProperty("resource").GetProperty("text").GetString()!
			: block.GetProperty("text").GetString()!;
}
