namespace Weavie.Core.Sessions;

/// <summary>A strict, side-effect-free snapshot of one persisted workspace session document.</summary>
public sealed record SessionStoreSnapshot {
	/// <summary>The workspace's persisted session descriptors.</summary>
	public required IReadOnlyList<SessionDescriptor> Items { get; init; }

	/// <summary>The last real shell width, or zero when none was recorded.</summary>
	public required int ShellColumns { get; init; }

	/// <summary>The last real shell height, or zero when none was recorded.</summary>
	public required int ShellRows { get; init; }
}
