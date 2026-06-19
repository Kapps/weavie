using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Weavie.Core.Diffs;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;

namespace Weavie.Win.Hosting;

/// <summary>
/// Production <see cref="IDiffPresenter"/>: renders an inbound <c>openDiff</c> as an editable Monaco
/// diff in the webview and blocks until the user resolves it. Each diff gets an id; the webview
/// replies with <c>diff-resolved</c>, which completes the awaiting task.
/// </summary>
public sealed class McpDiffPresenter : IDiffPresenter {
	private readonly HostBridge _bridge;
	private readonly IFileSystem _fileSystem;
	private readonly FileOpener _fileOpener;
	private readonly ConcurrentDictionary<string, TaskCompletionSource<DiffOutcome>> _pending = new(StringComparer.Ordinal);
	private int _counter;

	/// <summary>
	/// Creates the presenter, rendering diffs/files into the webview via <paramref name="bridge"/>
	/// and persisting kept edits through <paramref name="fileSystem"/>.
	/// </summary>
	public McpDiffPresenter(HostBridge bridge, IFileSystem fileSystem, FileOpener fileOpener) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(fileOpener);
		_bridge = bridge;
		_fileSystem = fileSystem;
		_fileOpener = fileOpener;
	}

	/// <inheritdoc/>
	public Task<DiffOutcome> PresentDiffAsync(DiffProposal proposal, CancellationToken cancellationToken) {
		string id = $"diff-{Interlocked.Increment(ref _counter)}";
		var tcs = new TaskCompletionSource<DiffOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = tcs;

		cancellationToken.Register(() => {
			if (_pending.TryRemove(id, out var pending)) {
				pending.TrySetCanceled();
				_bridge.PostToWeb($"{{\"type\":\"close-diff\",\"id\":\"{id}\"}}"); // id is a safe slug
			}
		});

		string original = _fileSystem.FileExists(proposal.OldFilePath) ? _fileSystem.ReadAllText(proposal.OldFilePath) : string.Empty;
		_bridge.PostToWeb(BuildShowDiff(id, proposal, original));
		return tcs.Task;
	}

	/// <inheritdoc/>
	public Task OpenFileAsync(string filePath, bool preview, CancellationToken cancellationToken) {
		_fileOpener.Open(filePath, line: 1, preview: preview, scratch: false);
		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	public Task CloseTabAsync(string filePath, CancellationToken cancellationToken) {
		_bridge.PostToWeb(BuildCloseTab(filePath));
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

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	/// <summary>Called when the webview replies with the user's Keep/Reject decision.</summary>
	public void Resolve(string id, bool kept, string? finalContents) {
		if (_pending.TryRemove(id, out var tcs)) {
			tcs.TrySetResult(kept ? DiffOutcome.Kept(finalContents ?? string.Empty) : DiffOutcome.Rejected());
		}
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
