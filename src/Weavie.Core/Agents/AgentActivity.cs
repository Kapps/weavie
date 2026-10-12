namespace Weavie.Core.Agents;

/// <summary>What kind of work a tool call does, in ACP's tool-kind vocabulary plus delegation.</summary>
public enum AgentActivityKind {
	/// <summary>Reads a file.</summary>
	Read,
	/// <summary>Edits or writes a file.</summary>
	Edit,
	/// <summary>Deletes a file.</summary>
	Delete,
	/// <summary>Moves or renames a file.</summary>
	Move,
	/// <summary>Searches files or the web.</summary>
	Search,
	/// <summary>Runs a command.</summary>
	Execute,
	/// <summary>Fetches a URL.</summary>
	Fetch,
	/// <summary>Hands work to a subagent.</summary>
	Delegate,
	/// <summary>Any other tool.</summary>
	Other,
	/// <summary>A tool described only by the provider's own title, which already reads as a phrase.</summary>
	Titled,
}

/// <summary>One tool call's work, reduced to a kind and a short subject (a file name, command, pattern, or tool name).</summary>
public sealed record AgentActivity(AgentActivityKind Kind, string Subject) {
	/// <summary>Builds an activity whose subject is the first line of <paramref name="text"/>, whitespace collapsed.</summary>
	public static AgentActivity Of(AgentActivityKind kind, string? text) {
		string line = (text ?? "").Split('\n', 2)[0];
		return new AgentActivity(kind, string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
	}

	/// <summary>Builds a file activity whose subject is <paramref name="path"/>'s file name.</summary>
	public static AgentActivity OfFile(AgentActivityKind kind, string? path) =>
		new(kind, Path.GetFileName((path ?? "").TrimEnd('/', '\\')));

	private static readonly Dictionary<AgentActivityKind, (string Doing, string Want)> Verbs = new() {
		[AgentActivityKind.Read] = ("reading", "read"),
		[AgentActivityKind.Edit] = ("editing", "edit"),
		[AgentActivityKind.Delete] = ("deleting", "delete"),
		[AgentActivityKind.Move] = ("moving", "move"),
		[AgentActivityKind.Search] = ("searching", "search"),
		[AgentActivityKind.Execute] = ("running", "run"),
		[AgentActivityKind.Fetch] = ("fetching", "fetch"),
		[AgentActivityKind.Delegate] = ("delegating", "delegate"),
		[AgentActivityKind.Other] = ("using", "use"),
	};

	/// <summary>The present-tense phrase, e.g. <c>running pnpm test</c>.</summary>
	public string Doing => Kind == AgentActivityKind.Titled ? Subject : Phrase(Verbs[Kind].Doing);

	/// <summary>The pending-approval phrase, e.g. <c>wants to run pnpm test</c>.</summary>
	public string Wants => Kind == AgentActivityKind.Titled ? $"needs approval: {Subject}" : "wants to " + Phrase(Verbs[Kind].Want);

	private string Phrase(string verb) => Subject.Length == 0 ? verb : $"{verb} {Subject}";
}

/// <summary>A tool call started, or is waiting for the user to approve it.</summary>
public sealed record AgentActivityObserved(AgentActivity Activity, bool AwaitingApproval) : AgentEvent;
