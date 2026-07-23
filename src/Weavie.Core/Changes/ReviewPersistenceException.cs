namespace Weavie.Core.Changes;

/// <summary>A durable review mutation that could not be checkpointed and therefore was not applied.</summary>
public sealed class ReviewPersistenceException : Exception {
	/// <summary>Creates an exception for a failed checkpoint operation.</summary>
	public ReviewPersistenceException(string message, Exception innerException) : base(message, innerException) { }
}
