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
	private readonly IHostBridge _bridge;
	private readonly IFileSystem _fileSystem;
	private readonly FileOpener _fileOpener;
	private readonly ConcurrentDictionary<string, TaskCompletionSource<DiffOutcome>> _pending = new(StringComparer.Ordinal);
	private int _counter;

	/// <summary>Creates a presenter that renders diffs over the bridge and delegates file opens to <paramref name="fileOpener"/>.</summary>
	public McpDiffPresenter(IHostBridge bridge, IFileSystem fileSystem, FileOpener fileOpener) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(fileOpener);
		_bridge = bridge;
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
				_bridge.PostToWeb($"{{\"type\":\"close-diff\",\"id\":\"{id}\"}}"); // id is a safe slug
			}
		});

		string original = _fileSystem.FileExists(proposal.OldFilePath) ? _fileSystem.ReadAllText(proposal.OldFilePath) : string.Empty;
		_bridge.PostToWeb(BuildShowDiff(id, proposal, original));
		return tcs.Task;
	}

	/// <summary>Reveals the file in Monaco (at line 1) in response to the MCP <c>openFile</c> tool.</summary>
	public Task OpenFileAsync(string filePath, CancellationToken cancellationToken) {
		_fileOpener.Open(filePath, line: 1);
		return Task.CompletedTask;
	}

	/// <summary>Called when the web view replies with the user's Keep/Reject decision.</summary>
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
