using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Diffs;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Production <see cref="IDiffPresenter"/>: renders an inbound <c>openDiff</c> as an editable Monaco
/// diff in the webview and blocks until the user resolves it. Each diff gets an id; the webview
/// replies with <c>diff-resolved</c>, which completes the awaiting task.
/// </summary>
public sealed class McpDiffPresenter : IDiffPresenter
{
    private readonly HostBridge _bridge;
    private readonly IFileSystem _fileSystem;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DiffOutcome>> _pending = new(StringComparer.Ordinal);
    private int _counter;

    public McpDiffPresenter(HostBridge bridge, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _bridge = bridge;
        _fileSystem = fileSystem;
    }

    public Task<DiffOutcome> PresentDiffAsync(DiffProposal proposal, CancellationToken cancellationToken)
    {
        var id = $"diff-{Interlocked.Increment(ref _counter)}";
        var tcs = new TaskCompletionSource<DiffOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetCanceled();
                _bridge.PostToWeb($"{{\"type\":\"close-diff\",\"id\":\"{id}\"}}"); // id is a safe slug
            }
        });

        var original = _fileSystem.FileExists(proposal.OldFilePath) ? _fileSystem.ReadAllText(proposal.OldFilePath) : string.Empty;
        _bridge.PostToWeb(BuildShowDiff(id, proposal, original));
        return tcs.Task;
    }

    public Task OpenFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var path = "\"" + JsonEncodedText.Encode(filePath) + "\"";
        _bridge.PostToWeb($"{{\"type\":\"open-file\",\"path\":{path}}}");
        return Task.CompletedTask;
    }

    /// <summary>Called when the webview replies with the user's Keep/Reject decision.</summary>
    public void Resolve(string id, bool kept, string? finalContents)
    {
        if (_pending.TryRemove(id, out var tcs))
        {
            tcs.TrySetResult(kept ? DiffOutcome.Kept(finalContents ?? string.Empty) : DiffOutcome.Rejected());
        }
    }

    private static string BuildShowDiff(string id, DiffProposal proposal, string original)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "show-diff");
            writer.WriteString("id", id);
            writer.WriteString("path", proposal.NewFilePath);
            writer.WriteString("tabName", proposal.TabName);
            writer.WriteString("original", original);
            writer.WriteString("proposed", proposal.NewFileContents);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
