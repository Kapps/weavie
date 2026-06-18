namespace Weavie.Core.Commands;

/// <summary>Thrown when a command id is not registered (strict: callers must use exact ids).</summary>
public sealed class UnknownCommandException : Exception {
	/// <summary>Creates the exception for the unregistered <paramref name="id"/> with the default guidance message.</summary>
	public UnknownCommandException(string id)
		: base($"Unknown command '{id}'. Call listCommands for the exact ids.") {
		Id = id;
	}

	/// <summary>Creates the exception with a custom message (e.g. one carrying near-match suggestions).</summary>
	public UnknownCommandException(string id, string message) : base(message) {
		Id = id;
	}

	/// <summary>The offending id.</summary>
	public string Id { get; }
}
