using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Weavie.Core.Diffs;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;

namespace Weavie.Hosting;

/// <summary>
/// The <see cref="IDiffPresenter"/> that renders an inbound <c>openDiff</c> as an editable Monaco diff in the
/// web view and blocks until the user resolves it. Each diff gets an id; the web view replies with
/// <c>diff-resolved</c>, which completes the awaiting task.
/// </summary>
public sealed class McpDiffPresenter : IDiffPresenter {
	private readonly SessionEditorChannel _channel;
	private readonly IFileSystem _fileSystem;
	private readonly FileOpener _fileOpener;
	private readonly ConcurrentDictionary<string, TaskCompletionSource<DiffOutcome>> _pending = new(StringComparer.Ordinal);
	// Process-wide so diff ids are unique across ALL sessions: the host routes diff-resolved back to the owning
	// session by id alone, so two sessions minting "diff-1" would let a mid-resolve switch resolve the wrong diff.
	private static int _counter;

	/// <summary>Renders diffs through the session's editor <paramref name="channel"/> (so a muted session's diff is held, not posted into the foreground) and delegates file opens to <paramref name="fileOpener"/>.</summary>
	public McpDiffPresenter(SessionEditorChannel channel, IFileSystem fileSystem, FileOpener fileOpener) {
		ArgumentNullException.ThrowIfNull(channel);
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(fileOpener);
		_channel = channel;
		_fileSystem = fileSystem;
		_fileOpener = fileOpener;
	}

	/// <summary>
	/// Assigns the proposal an id, pushes a <c>show-diff</c> to the web view, and returns a task that
	/// completes when the user resolves it (or is cancelled, which also closes the diff in the UI).
	/// </summary>
	public Task<DiffOutcome> PresentDiffAsync(DiffProposal proposal, CancellationToken cancellationToken) {
		ArgumentNullException.ThrowIfNull(proposal);
		string id = $"diff-{Interlocked.Increment(ref _counter)}";
		var tcs = new TaskCompletionSource<DiffOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = tcs;

		// Tear the diff out of the page (if it's the active session's, so it's actually rendered there).
		cancellationToken.Register(() => Abandon(id));

		string original = _fileSystem.FileExists(proposal.OldFilePath) ? _fileSystem.ReadAllText(proposal.OldFilePath) : string.Empty;
		// Held by the channel: rendered now if this session is active, else surfaced when it's switched in.
		_channel.ShowDiff(id, BuildShowDiff(id, proposal, original));
		return tcs.Task;
	}

	/// <summary>Reveals the file in Monaco in response to the MCP <c>openFile</c> tool (preview or persistent).</summary>
	public Task OpenFileAsync(string filePath, bool preview, CancellationToken cancellationToken) {
		_fileOpener.Open(filePath, line: 1, preview: preview, scratch: false);
		return Task.CompletedTask;
	}

	/// <summary>Asks the webview to close the file's tab (the MCP <c>close_tab</c> tool).</summary>
	public Task CloseTabAsync(string filePath, CancellationToken cancellationToken) {
		_channel.Reveal(BuildCloseTab(filePath));
		return Task.CompletedTask;
	}

	private static string BuildCloseTab(string path) {
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteString("type", "close-tab");
			writer.WriteString("path", path);
			writer.WriteEndObject();
		}

		return System.Text.Encoding.UTF8.GetString(stream.ToArray());
	}

	/// <summary>
	/// Abandons every still-pending openDiff when Claude flips into an auto-apply mode (e.g. Shift+Tab in the TUI):
	/// a leftover blocking review would strand its model over the editor and block the post-turn review. Cancels
	/// each awaiting task (the MCP server then sends no response) and closes the stale review in the page.
	/// </summary>
	public void DismissPending() {
		foreach (string id in _pending.Keys) {
			Abandon(id);
		}
	}

	// Drops a pending diff and, when it's the active session's rendered one, tears it out of the page. Shared by
	// the per-request cancellation token and the auto-apply dismissal above.
	private void Abandon(string id) {
		if (_pending.TryRemove(id, out var pending)) {
			pending.TrySetCanceled();
			_channel.EndDiff(id, closeInUi: true);
		}
	}

	/// <summary>
	/// Settles the user's Keep/Reject decision from the web view. Returns <c>true</c> when this presenter owned
	/// <paramref name="id"/>, so the caller can route <c>diff-resolved</c> across sessions and flag an unowned id.
	/// </summary>
	public bool Resolve(string id, bool kept, string? finalContents) {
		if (!_pending.TryRemove(id, out var tcs)) {
			return false;
		}

		tcs.TrySetResult(kept ? DiffOutcome.Kept(finalContents ?? string.Empty) : DiffOutcome.Rejected());
		// The page already closed its own review when the user resolved it, so just stop tracking the diff.
		_channel.EndDiff(id, closeInUi: false);
		return true;
	}

	private static string BuildShowDiff(string id, DiffProposal proposal, string original) {
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteString("type", "show-diff");
			writer.WriteString("id", id);
			writer.WriteString("path", proposal.NewFilePath);
			writer.WriteString("tabName", proposal.TabName);
			writer.WriteString("original", original);
			writer.WriteString("proposed", proposal.NewFileContents);
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
