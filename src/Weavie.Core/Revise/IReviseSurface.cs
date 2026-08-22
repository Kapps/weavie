namespace Weavie.Core.Revise;

/// <summary>Where a session's in-flight revisions become visible, and where their failures reach the user.</summary>
public interface IReviseSurface {
	/// <summary>Replaces the published in-flight set, so a reconnecting client renders the current state.</summary>
	void Publish(IReadOnlyList<ReviseRegion> inFlight);

	/// <summary>
	/// Asks the editor holding <paramref name="region"/> whether the write may land, answering null to allow it or
	/// the reason it must not. A dirty buffer refuses: VS Code skips resolving a dirty model, so the write would be
	/// dropped and then overwritten by the next autosave.
	/// </summary>
	Task<string?> ConfirmAsync(ReviseRegion region, CancellationToken cancellationToken);

	/// <summary>Tells the user a region's revision failed, and why.</summary>
	void Failed(ReviseRegion region, string reason);
}
