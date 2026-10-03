using System.Globalization;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

/// <summary>What a primary conversation keeps when it rewinds to just before one prompt.</summary>
internal sealed record AcpRewindPlan(
	long Turn,
	IReadOnlyList<AgentPaneMessage> Kept,
	string Prompt,
	string? ForkMessageId) {
	/// <summary>Cuts the display journal at the primary turn <paramref name="turnId"/>.</summary>
	public static AcpRewindPlan Create(IReadOnlyList<AgentPaneMessage> journal, string turnId) {
		ArgumentNullException.ThrowIfNull(journal);
		int cut = LastIndexOf(journal, journal.Count, message => message.Type == "turn-started" && message.TurnId == turnId);
		if (cut < 0 || !long.TryParse(turnId, CultureInfo.InvariantCulture, out long turn)) {
			throw new InvalidOperationException("That prompt is no longer in this conversation.");
		}
		int prior = LastIndexOf(journal, cut, message => message.Type == "turn-started");
		string? fork = null;
		if (prior >= 0) {
			int answer = LastIndexOf(journal, cut, message => message.Type == "item-completed"
				&& message.ItemType == "agentMessage" && message.MessageId is not null);
			fork = answer > prior ? journal[answer].MessageId : throw new InvalidOperationException(
				"The previous prompt has no recorded agent reply to rewind to. Rewind further back.");
		}
		var after = journal.Skip(cut);
		return new(
			turn,
			[.. journal.Take(cut), .. after.Where(message => message.ConversationId is not null && AnchoredBefore(message.AnchorTurnId, turn))],
			after.FirstOrDefault(message => message.ConversationId is null && message.TurnId == turnId
				&& message.Type is "user-message" or "user-command")?.Text ?? string.Empty,
			fork);
	}

	/// <summary>Whether a saved plan or side conversation belongs to the kept history.</summary>
	public bool Keeps(long turn) => turn < Turn;

	private static bool AnchoredBefore(string? anchor, long turn) =>
		long.TryParse(anchor, CultureInfo.InvariantCulture, out long value) && value < turn;

	private static int LastIndexOf(IReadOnlyList<AgentPaneMessage> journal, int end, Func<AgentPaneMessage, bool> primary) {
		for (int index = end - 1; index >= 0; index--) {
			if (journal[index].ConversationId is null && primary(journal[index])) return index;
		}
		return -1;
	}
}
