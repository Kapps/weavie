using Weavie.Core.Diffs;
using Weavie.Core.Editor;
using Weavie.Core.Mcp;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

/// <summary>
/// The <see cref="IDiffPresenter"/> that renders an inbound <c>openDiff</c> as an editable Monaco diff in the
/// web view and blocks until the user resolves it. Each diff gets an id; the web view replies with
/// <c>diff-resolved</c>, which completes the awaiting task.
/// </summary>
public sealed class McpDiffPresenter : IDiffPresenter {
	private readonly MessageFeatureChannel _editor;
	private readonly FileProviderService _files;
	private readonly FileOpener _fileOpener;
	private readonly Action<string> _closeFile;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, PendingDiff> _pending = new(StringComparer.Ordinal);
	private int _counter;

	/// <summary>Renders diffs through the owning session's <paramref name="editor"/> channel, reads the baseline
	/// through <paramref name="files"/>, and delegates file opens to <paramref name="fileOpener"/>.</summary>
	public McpDiffPresenter(
		MessageFeatureChannel editor,
		FileProviderService files,
		FileOpener fileOpener,
		Action<string> closeFile) {
		ArgumentNullException.ThrowIfNull(editor);
		ArgumentNullException.ThrowIfNull(files);
		ArgumentNullException.ThrowIfNull(fileOpener);
		ArgumentNullException.ThrowIfNull(closeFile);
		_editor = editor;
		_files = files;
		_fileOpener = fileOpener;
		_closeFile = closeFile;
	}

	/// <summary>
	/// Assigns the proposal an id, pushes a <c>show-diff</c> to the web view, and returns a task that
	/// completes when the user resolves it (or is cancelled, which also closes the diff in the UI).
	/// </summary>
	public Task<DiffOutcome> PresentDiffAsync(DiffProposal proposal, CancellationToken cancellationToken) {
		ArgumentNullException.ThrowIfNull(proposal);
		string id = $"diff-{Interlocked.Increment(ref _counter)}";
		var tcs = new TaskCompletionSource<DiffOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
		string original = _files.ReadIfAllowed(proposal.OldFilePath) ?? string.Empty;
		var wire = new DiffWire(
			id,
			proposal.NewFilePath,
			proposal.TabName,
			original,
			proposal.NewFileContents);
		lock (_gate) {
			if (_pending.Count > 0) {
				throw new InvalidOperationException("This session already has a diff awaiting review.");
			}

			_pending.Add(id, new PendingDiff(tcs, wire));
			_editor.Publish("showDiff", wire);
		}

		cancellationToken.Register(() => Abandon(id));
		return tcs.Task;
	}

	internal void Replay(MessageTargetFeature target) {
		ArgumentNullException.ThrowIfNull(target);
		lock (_gate) {
			target.Publish("diffSnapshot", new {
				proposals = _pending.Values.Select(pending => pending.Wire).ToArray(),
			});
		}
	}

	/// <summary>Reveals the file in Monaco in response to the MCP <c>openFile</c> tool (preview or persistent).</summary>
	public Task OpenFileAsync(string filePath, bool preview, CancellationToken cancellationToken) =>
		_fileOpener.OpenAsync(
			filePath,
			line: 1,
			preview: preview,
			scratch: false,
			cancellationToken);

	/// <summary>Asks the webview to close the file's tab (the MCP <c>close_tab</c> tool).</summary>
	public Task CloseTabAsync(string filePath, CancellationToken cancellationToken) {
		_closeFile(filePath);
		return Task.CompletedTask;
	}

	/// <summary>
	/// Abandons every still-pending openDiff when Claude flips into an auto-apply mode (e.g. Shift+Tab in the TUI):
	/// a leftover blocking review would strand its model over the editor and block the post-turn review. Cancels
	/// each awaiting task (the MCP server then sends no response) and closes the stale review in the page.
	/// </summary>
	public void DismissPending() {
		string[] ids;
		lock (_gate) {
			ids = [.. _pending.Keys];
		}
		foreach (string id in ids) {
			Abandon(id);
		}
	}

	// Shared by per-request cancellation and auto-apply dismissal.
	private void Abandon(string id) {
		PendingDiff? pending;
		lock (_gate) {
			if (!_pending.Remove(id, out pending)) {
				return;
			}

			_editor.Publish("closeDiff", new { id });
		}

		pending.Completion.TrySetCanceled();
	}

	/// <summary>
	/// Settles the user's Keep/Reject decision from the web view. Returns <c>true</c> when this presenter owned
	/// <paramref name="id"/>, so the caller can route <c>diff-resolved</c> across sessions and flag an unowned id.
	/// </summary>
	public bool Resolve(string id, bool kept, string? finalContents) {
		PendingDiff? pending;
		lock (_gate) {
			if (!_pending.Remove(id, out pending)) {
				return false;
			}

			_editor.Publish("closeDiff", new { id });
		}

		pending.Completion.TrySetResult(
			kept
				? DiffOutcome.Kept(finalContents ?? string.Empty)
				: DiffOutcome.Rejected());
		return true;
	}

	private sealed record PendingDiff(
		TaskCompletionSource<DiffOutcome> Completion,
		DiffWire Wire);

	private sealed record DiffWire(
		string Id,
		string Path,
		string TabName,
		string Original,
		string Proposed);
}
