using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpVisualPreferenceTests {
	[Fact]
	public async Task ThePreferenceFollowsTheUsersTextForTheAgentOnly() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		fixture.PreferVisuals(true);

		await fixture.TurnAsync("compare the options", 1);

		JsonElement[] blocks = [.. AcpPromptAssertions.Blocks(Assert.Single(AcpPromptAssertions.Read(fixture)))];
		Assert.Equal("compare the options", blocks[0].GetProperty("text").GetString());
		Assert.Equal(AgentSettings.VisualPreference, blocks[1].GetProperty("text").GetString());
		Assert.Equal("assistant", Assert.Single(blocks[1].GetProperty("annotations").GetProperty("audience").EnumerateArray()).GetString());
		Assert.Equal("compare the options", Assert.Single(fixture.Messages, message => message.Type == "user-message").Text);
	}

	[Fact]
	public async Task TheSettingIsReadForEachPrompt() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		fixture.PreferVisuals(true);
		await fixture.TurnAsync("first", 1);
		fixture.PreferVisuals(false);
		await fixture.TurnAsync("second", 2);

		Assert.Equal([true, false], AcpPromptAssertions.Read(fixture).Select(CarriesPreference));
	}

	[Fact]
	public async Task SlashCommandsAndSessionMessagesGoWithoutThePreference() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		fixture.PreferVisuals(true);

		fixture.Session.Submit(AgentTurnSubmission.Prompt("ping") with { Sender = new AgentMessageSender("feat/other", "Other") });
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");
		fixture.SubmitCommand("compact", "/compact");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		await fixture.TurnAsync("/a-command-the-agent-does-not-list", 3);

		Assert.Equal([false, false, false], AcpPromptAssertions.Read(fixture).Select(CarriesPreference));
	}

	private static bool CarriesPreference(JsonElement request) => AcpPromptAssertions.Blocks(request).Any(block =>
		block.GetProperty("type").GetString() == "text" && block.GetProperty("text").GetString() == AgentSettings.VisualPreference);
}
