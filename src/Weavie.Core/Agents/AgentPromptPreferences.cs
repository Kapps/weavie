using Weavie.Core.Configuration;

namespace Weavie.Core.Agents;

/// <summary>What the user's settings ask of an agent, said in the user's voice at the end of their prompt.</summary>
public static class AgentPromptPreferences {
	/// <summary>Asks for findings on the board; without it agents don't call the tool.</summary>
	public const string PinAsk = "Pin what you find to the board as you go. I read it while I wait, so don't repeat a pin in your reply; refer to it.";

	/// <summary>Describes the reader, and keeps brevity from cutting the work or the caveats.</summary>
	public const string AdhdReader = "I have ADHD. I lose the thread in long paragraphs and dense wording, so keep what you write to me brief and plain. "
		+ "Brief is about the words: do the work as thoroughly as you otherwise would, and still tell me the caveats and gaps.";

	/// <summary>The enabled sentences for one prompt, or null; <paramref name="board"/> is false where the conversation has no board.</summary>
	public static string? Compose(SettingsStore settings, bool board) {
		ArgumentNullException.ThrowIfNull(settings);
		string?[] asks = [
			board && settings.RequireBool(AgentSettings.FindingsBoard) ? PinAsk : null,
			settings.RequireBool(AgentSettings.AdhdReader) ? AdhdReader : null,
		];
		return asks.OfType<string>().ToArray() is { Length: > 0 } enabled ? string.Join(" ", enabled) : null;
	}
}
