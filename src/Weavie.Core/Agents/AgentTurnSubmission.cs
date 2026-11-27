namespace Weavie.Core.Agents;

/// <summary>One thing attached to a structured agent turn: a staged image, or a selection the user pointed at.</summary>
public abstract record AgentInputAttachment {
	private AgentInputAttachment() { }

	/// <summary>The client-generated attachment id, scoped to one Weavie session.</summary>
	public required string Id { get; init; }

	/// <summary>An image the owning host has staged on disk.</summary>
	public sealed record Image : AgentInputAttachment {
		/// <summary>The absolute path on the provider's host.</summary>
		public required string Path { get; init; }

		/// <summary>The validated image MIME type.</summary>
		public required string Mime { get; init; }
	}

	/// <summary>Values the user selected in the pane (chart points, table cells, reply text), as the agent reads them.</summary>
	public sealed record Selection : AgentInputAttachment {
		/// <summary>The most characters a selection's text may hold.</summary>
		public const int MaxTextLength = AgentBlockLimits.ArgumentBytes;

		/// <summary>The chip label, such as the block title and range.</summary>
		public required string Label { get; init; }

		/// <summary>The selected values, as text.</summary>
		public required string Text { get; init; }
	}
}

/// <summary>The semantic kind of one structured-agent submission.</summary>
public enum AgentTurnSubmissionKind {
	/// <summary>An ordinary user prompt.</summary>
	Prompt,

	/// <summary>One provider-advertised slash command.</summary>
	ProviderCommand,

	/// <summary>A bundled MCP prompt selected by the user.</summary>
	McpPrompt,
}

/// <summary>The Weavie session whose agent sent a message to another session.</summary>
public sealed record AgentMessageSender(string Id, string Title);

/// <summary>An atomic structured-agent input: text and the exact staged images for the turn.</summary>
public sealed record AgentTurnSubmission {
	/// <summary>An ordinary text prompt with a fresh id.</summary>
	public static AgentTurnSubmission Prompt(string text) => new() {
		Id = Guid.NewGuid().ToString("n"),
		Text = text,
		Kind = AgentTurnSubmissionKind.Prompt,
		CommandName = string.Empty,
		Attachments = [],
	};

	/// <summary>The client-generated submission id.</summary>
	public required string Id { get; init; }

	/// <summary>The prompt text; empty is valid when at least one attachment is present.</summary>
	public required string Text { get; init; }

	/// <summary>Whether the text is an ordinary prompt or a provider-advertised command.</summary>
	public required AgentTurnSubmissionKind Kind { get; init; }

	/// <summary>The exact provider command or bundled MCP prompt name; empty for an ordinary prompt.</summary>
	public required string CommandName { get; init; }

	/// <summary>The staged images and selections submitted with the text.</summary>
	public required IReadOnlyList<AgentInputAttachment> Attachments { get; init; }

	/// <summary>The session that sent this prompt, when another session's agent sent it rather than the user.</summary>
	public AgentMessageSender? Sender { get; init; }

	/// <summary>Where the prompt was asked from, when it was asked about a selection a surface places it under.</summary>
	public AgentAnchor? Anchor { get; init; }

	/// <summary>Whether a provider command also carries the session's per-turn context, as a workflow stage's opening
	/// turn does; ordinary prompts always carry it.</summary>
	public bool CarriesTurnContext { get; init; }

	/// <summary>
	/// The turn as a terminal agent takes it: the text and each image's path, which the agent attaches. A command's
	/// text leads so its slash opens the input; otherwise the images lead the text.
	/// </summary>
	public IReadOnlyList<string> TerminalParts() {
		var paths = Attachments.OfType<AgentInputAttachment.Image>().Select(attachment => attachment.Path);
		return Kind == AgentTurnSubmissionKind.ProviderCommand ? [Text, .. paths] : [.. paths, Text];
	}
}

/// <summary>
/// Where a prompt was asked from and the text selected there: lines of the file <paramref name="Path"/>, or a place in
/// walkthrough <paramref name="BlockId"/> of conversation <paramref name="ConversationId"/> (null for the primary).
/// <paramref name="Place"/> is the asking surface's own, which only it reads; <paramref name="Card"/> is the first
/// prompt of the card the prompt belongs to, which its follow-ups repeat.
/// </summary>
public sealed partial record AgentAnchor(string? Path, string? ConversationId, string? BlockId, string Place, string Quote, string Card) {
	/// <summary>The most characters of the selected text the anchor quotes.</summary>
	public const int QuoteLength = 200;

	/// <summary>Why this anchor can't be kept, or null: it names a file or a walkthrough, a place and card are short
	/// paths, a quote one line of text.</summary>
	public string? Problem() =>
		(Path is null) == (BlockId is null) ? "An anchor names a file or a walkthrough."
		: Path?.Length == 0 ? "An anchor's file needs a path."
		: !PlaceRule().IsMatch(Place) || !PlaceRule().IsMatch(Card) ? "An anchor's place and card are up to 200 letters, digits, '.', '_', '-', '@' or '/'."
		: Quote.Trim().Length == 0 || Quote.Length > QuoteLength || Quote.Contains('\n') ? $"An anchor's quote is one line of up to {QuoteLength} characters."
		: null;

	[System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_.@/-]{1,200}$")]
	private static partial System.Text.RegularExpressions.Regex PlaceRule();
}
