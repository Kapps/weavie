namespace Weavie.Hosting.Messaging;

internal sealed record MessageExecutionPolicy(TimeSpan SlowAfter, TimeSpan Deadline) {
	public static MessageExecutionPolicy Default { get; } = new(
		TimeSpan.FromSeconds(2),
		TimeSpan.FromSeconds(Core.Configuration.MessageSettings.DefaultOperationDeadlineSeconds));

	public void Validate() {
		if (SlowAfter <= TimeSpan.Zero) {
			throw new ArgumentOutOfRangeException(nameof(SlowAfter), "The slow-operation threshold must be positive.");
		}

		if (Deadline <= SlowAfter) {
			throw new ArgumentOutOfRangeException(nameof(Deadline), "The message deadline must exceed the slow threshold.");
		}
	}
}
