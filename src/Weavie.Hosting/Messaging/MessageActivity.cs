using Weavie.Core.Commands;

namespace Weavie.Hosting.Messaging;

/// <summary>What an operation is doing in user terms, e.g. "Saving a file", and what its deadline means.</summary>
public sealed record MessageActivity {
	private MessageActivity(string label, OperationSeverity severity) {
		ArgumentException.ThrowIfNullOrEmpty(label);
		Label = label;
		Severity = severity;
	}

	/// <summary>The user-facing description.</summary>
	public string Label { get; }

	/// <summary>What reaching the deadline means.</summary>
	public OperationSeverity Severity { get; }

	/// <summary>Builds an activity with an explicit <paramref name="severity"/>.</summary>
	public static MessageActivity Of(string label, OperationSeverity severity) => new(label, severity);

	/// <summary>A read-only activity a later run supersedes.</summary>
	public static MessageActivity Droppable(string label) => new(label, OperationSeverity.Droppable);

	/// <summary>An activity the user can retry.</summary>
	public static MessageActivity ActionFailed(string label) => new(label, OperationSeverity.ActionFailed);

	/// <summary>An activity the session's state depends on.</summary>
	public static MessageActivity SessionCritical(string label) => new(label, OperationSeverity.SessionCritical);

	/// <summary>An activity that must not be cancelled at the deadline.</summary>
	public static MessageActivity NoDeadline(string label) => new(label, OperationSeverity.NoDeadline);
}
