namespace Weavie.Runner;

/// <summary>A point-in-time view of the updater for the runner status page (built even when updates are off).</summary>
public sealed record UpdateStatus {
	/// <summary>Whether <c>--auto-update</c> is on.</summary>
	public required bool Enabled { get; init; }

	/// <summary>The selected channel, or off when updates are disabled.</summary>
	public string Channel { get; init; } = "off";

	/// <summary>The runner's own build identity.</summary>
	public required string RunnerBuild { get; init; }

	/// <summary>The staged (current-symlink) build, when a managed version exists.</summary>
	public int? Staged { get; init; }

	/// <summary>The last build confirmed serving.</summary>
	public int? Confirmed { get; init; }

	/// <summary>
	/// What the updater is doing: <c>idle</c>, <c>updating</c>, <c>rolled-back</c>, <c>failed</c>,
	/// or <c>error</c>.
	/// </summary>
	public required string Phase { get; init; }

	/// <summary>Human detail for the phase (the hold, the error, the rollback), when there is one.</summary>
	public string? Detail { get; init; }

	/// <summary>True when the runner executes an older version dir than <c>current</c> — a restart applies it.</summary>
	public bool RunnerBehind { get; init; }
}

