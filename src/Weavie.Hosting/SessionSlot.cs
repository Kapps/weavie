using Weavie.Core.Editor;

namespace Weavie.Hosting;

/// <summary>
/// One entry on the session rail: a checkout, loaded (a running
/// <see cref="HostSession"/>) or dormant (just the worktree on disk, shown faded, loaded on click). The
/// <see cref="Id"/> is stable and independent of any <see cref="HostSession"/> so a chip keeps its place.
/// </summary>
public sealed class SessionSlot {
	/// <summary>Stable rail identity, independent of a live runtime incarnation.</summary>
	public required string Id { get; init; }

	/// <summary>Branch (or folder leaf) name — drives the chip's deterministic hue + monogram.</summary>
	public required string Label { get; init; }

	/// <summary>The directory a <see cref="HostSession"/> is (or would be) rooted at.</summary>
	public required string WorktreePath { get; init; }

	/// <summary>Whether Weavie owns and may remove this checkout.</summary>
	public required bool ManagedCheckout { get; init; }

	/// <summary>The provider this session uses. Existing sessions default to Claude.</summary>
	public required string AgentProviderId { get; init; }

	/// <summary>The live backend, or <c>null</c> when this slot is unloaded (dormant).</summary>
	public HostSession? Session { get; set; }

	/// <summary>The editor state restored when this slot is loaded.</summary>
	public required EditorSession EditorSession { get; set; }

	/// <summary>True when the slot has a live backend.</summary>
	public bool Loaded => Session is not null;
}
