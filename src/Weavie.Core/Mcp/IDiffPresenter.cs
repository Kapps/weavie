using Weavie.Core.Diffs;

namespace Weavie.Core.Mcp;

/// <summary>
/// Surfaces an inbound <c>openDiff</c> to the user and resolves it. Blocking — the MCP response is withheld
/// until the user's Keep/Reject (in tests, scripted).
/// </summary>
public interface IDiffPresenter {
	/// <summary>
	/// Presents the proposed diff and resolves to the outcome. On <see cref="DiffResult.Kept"/>,
	/// <see cref="DiffOutcome.FinalContents"/> holds the (possibly user-edited) content to persist.
	/// </summary>
	Task<DiffOutcome> PresentDiffAsync(DiffProposal proposal, CancellationToken cancellationToken);

	/// <summary>
	/// Reveals a file in the editor (the MCP <c>openFile</c> tool). <paramref name="preview"/> opens it as a
	/// reusable preview tab; <c>false</c> opens a persistent tab. <paramref name="rendered"/> shows the file's
	/// rendered view when it has one. <paramref name="focus"/> selects the tab and activates the editor in its session.
	/// False when the file couldn't be opened.
	/// </summary>
	Task<bool> OpenFileAsync(string filePath, bool preview, bool rendered, bool focus, CancellationToken cancellationToken);

	/// <summary>Closes the editor tab for <paramref name="filePath"/> (the MCP <c>close_tab</c> tool).</summary>
	Task CloseTabAsync(string filePath, CancellationToken cancellationToken);

	/// <summary>
	/// Opens a tab of <paramref name="kind"/> at <paramref name="path"/> that renders state the session owns, such as an
	/// agent's dashboard. Without <paramref name="focus"/> it never takes the editor from what the user is doing in it.
	/// </summary>
	Task OpenTabAsync(string path, string kind, bool focus, CancellationToken cancellationToken);
}

/// <summary>Scripted <see cref="IDiffPresenter"/> for tests: returns a preset outcome and records calls.</summary>
public sealed class FakeDiffPresenter : IDiffPresenter {
	private readonly Func<DiffProposal, DiffOutcome> _decide;

	/// <summary>Creates a presenter that resolves each proposal via <paramref name="decide"/>.</summary>
	public FakeDiffPresenter(Func<DiffProposal, DiffOutcome> decide) {
		_decide = decide;
	}

	/// <summary>Keeps every diff, saving the proposed contents unchanged.</summary>
	public static FakeDiffPresenter AlwaysKeep() =>
		new(p => DiffOutcome.Kept(p.NewFileContents));

	/// <summary>Rejects every diff.</summary>
	public static FakeDiffPresenter AlwaysReject() => new(_ => DiffOutcome.Rejected());

	/// <summary>Every proposal passed to <see cref="PresentDiffAsync"/>, in order, for test assertions.</summary>
	public List<DiffProposal> Presented { get; } = [];

	/// <summary>Every request passed to <see cref="OpenFileAsync"/>, in order, for test assertions.</summary>
	public List<(string Path, bool Preview, bool Rendered, bool Focus)> Opened { get; } = [];

	/// <summary>Every path passed to <see cref="CloseTabAsync"/>, in order, for test assertions.</summary>
	public List<string> ClosedTabs { get; } = [];

	/// <summary>Every request passed to <see cref="OpenTabAsync"/>, in order, for test assertions.</summary>
	public List<(string Path, string Kind, bool Focus)> OpenedTabs { get; } = [];

	/// <inheritdoc/>
	public Task<DiffOutcome> PresentDiffAsync(DiffProposal proposal, CancellationToken cancellationToken) {
		Presented.Add(proposal);
		return Task.FromResult(_decide(proposal));
	}

	/// <inheritdoc/>
	public Task<bool> OpenFileAsync(string filePath, bool preview, bool rendered, bool focus, CancellationToken cancellationToken) {
		Opened.Add((filePath, preview, rendered, focus));
		return Task.FromResult(true);
	}

	/// <inheritdoc/>
	public Task CloseTabAsync(string filePath, CancellationToken cancellationToken) {
		ClosedTabs.Add(filePath);
		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	public Task OpenTabAsync(string path, string kind, bool focus, CancellationToken cancellationToken) {
		OpenedTabs.Add((path, kind, focus));
		return Task.CompletedTask;
	}
}
