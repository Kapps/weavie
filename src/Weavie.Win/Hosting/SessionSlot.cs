namespace Weavie.Win.Hosting;

/// <summary>
/// One entry on the session rail: a worktree (or the primary checkout) that may or may not have a live
/// backend. A <em>loaded</em> slot has a running <see cref="HostSession"/> (Claude/shell/LSP/MCP); an
/// <em>unloaded</em> (dormant) slot is just the worktree on disk, shown faded — clicking it loads on demand.
/// The <see cref="Id"/> is stable and independent of any <see cref="HostSession"/> so a chip keeps its place
/// across load/unload (and so a future idle-unload can fade a chip in place rather than re-create it).
/// </summary>
internal sealed class SessionSlot {
	/// <summary>Stable rail id: the primary session's id for the primary; the branch name for a worktree.</summary>
	public required string Id { get; init; }

	/// <summary>Branch (or folder leaf) name — drives the chip's deterministic hue + monogram.</summary>
	public required string Label { get; init; }

	/// <summary>The directory a <see cref="HostSession"/> is (or would be) rooted at.</summary>
	public required string WorktreePath { get; init; }

	/// <summary>The workspace's own checkout — always loaded, never unloadable.</summary>
	public required bool IsPrimary { get; init; }

	/// <summary>The live backend, or <c>null</c> when this slot is unloaded (dormant).</summary>
	public HostSession? Session { get; set; }

	/// <summary>When this slot was last made active (UTC) — the signal a future idle-unload sweep reads.</summary>
	public DateTimeOffset LastActiveUtc { get; set; }

	/// <summary>True when the slot has a live backend.</summary>
	public bool Loaded => Session is not null;
}
