using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Weavie.Core.Diffs;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;

namespace Weavie.Hosting;

/// <summary>
/// Production <see cref="IDiffPresenter"/>: renders an inbound <c>openDiff</c> as an editable Monaco
/// diff in the web view and blocks until the user resolves it. Each diff gets an id; the web view
/// replies with <c>diff-resolved</c>, which completes the awaiting task.
/// </summary>
public sealed class McpDiffPresenter : IDiffPresenter {
	private readonly SessionEditorChannel _channel;
	private readonly IFileSystem _fileSystem;
	private readonly FileOpener _fileOpener;
	private readonly ConcurrentDictionary<string, TaskCompletionSource<DiffOutcome>> _pending = new(StringComparer.Ordinal);
	// Process-wide so diff ids are unique across ALL sessions, not just within one presenter. The page replies
	// with diff-resolved carrying only the id; the host routes it back to the OWNING session by that id, so two
	// sessions must never both mint "diff-1" (a switch between render and resolve would otherwise resolve the
	// wrong session's diff with the wrong contents).
	private static int _counter;

	/// <summary>Creates a presenter that renders diffs through the session's editor <paramref name="channel"/> (so a muted session's diff is held, not posted into the foreground) and delegates file opens to <paramref name="fileOpener"/>.</summary>
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

		cancellationToken.Register(() => {
			if (_pending.TryRemove(id, out var pending)) {
				pending.TrySetCanceled();
				// Tear the diff out of the page (if it's the active session's, so it's actually rendered there).
				_channel.EndDiff(id, closeInUi: true);
			}
		});

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
	/// Called when the web view replies with the user's Keep/Reject decision. Returns <c>true</c> when this
	/// presenter owned <paramref name="id"/> (so the caller can route a <c>diff-resolved</c> across sessions
	/// and loudly flag an id no session owns — a switch-race or double-resolve).
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
