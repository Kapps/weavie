namespace Weavie.Core.Commands;

/// <summary>What reaching the message deadline means for an operation: can the session still be trusted without it?</summary>
public enum OperationSeverity {
	/// <summary>Read-only and superseded by a later run; it is dropped quietly and its lane moves on.</summary>
	Droppable,

	/// <summary>The user is told and can retry; the session is fine.</summary>
	ActionFailed,

	/// <summary>The session's state can no longer be trusted, so it requires a reload.</summary>
	SessionCritical,

	/// <summary>Cancelling would be worse than waiting; the user is told it is still running and can cancel it.</summary>
	NoDeadline,
}
