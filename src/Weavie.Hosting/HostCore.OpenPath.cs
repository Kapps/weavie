using System.Text.Json;
using Weavie.Core.Commands;

namespace Weavie.Hosting;

// Opening a path the OS delivered. The request can land before the page exists — a cold launch resolves its
// workspace from the path itself — so it waits for the session rather than being dropped.
public sealed partial class HostCore {
	private readonly Lock _openPathGate = new();
	private readonly List<PendingOpen> _pendingOpenPaths = [];
	private bool _openPathPageReady;

	/// <summary>
	/// Reveals <paramref name="path"/> at <paramref name="line"/> in this workspace once its page can receive
	/// the push. A cold launch resolves its workspace from the path, so the request routinely arrives before
	/// there is anything to push to, and Broadcast drops rather than buffers a push with no client.
	/// </summary>
	public void RequestOpenPath(string path, int line) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_openPathGate) {
			_pendingOpenPaths.Add(new PendingOpen(path, Math.Max(1, line)));
		}

		FlushPendingOpenPaths();
	}

	// Called from the page's `ready` handler, once its bridge can actually receive a push.
	private void MarkOpenPathPageReady() {
		lock (_openPathGate) {
			_openPathPageReady = true;
		}

		FlushPendingOpenPaths();
	}

	private void FlushPendingOpenPaths() {
		if (_sessions?.Slots.FirstOrDefault(IsWorkspaceCheckout)?.Session is not { } session) {
			return;
		}

		PendingOpen[] pending;
		lock (_openPathGate) {
			if (!_openPathPageReady || _pendingOpenPaths.Count == 0) {
				return;
			}

			pending = [.. _pendingOpenPaths];
			_pendingOpenPaths.Clear();
		}

		foreach (var open in pending) {
			session.FileOpener.Open(open.Path, open.Line, preview: false, scratch: false);
		}
	}

	private readonly record struct PendingOpen(string Path, int Line);

	private void RegisterOpenFileHandler(HostSession session) =>
		session.Commands.RegisterHandler(CoreCommands.OpenFile, (argsJson, _) =>
			Task.FromResult(OpenFileFromArgs(session, argsJson)));

	private static CommandResult OpenFileFromArgs(HostSession session, string? argsJson) {
		if (string.IsNullOrWhiteSpace(argsJson)) {
			return CommandResult.Failure("No file to open — pass an absolute path.");
		}

		string path;
		int line;
		try {
			using var document = JsonDocument.Parse(argsJson);
			var root = document.RootElement;
			path = root.TryGetProperty("path", out var pathValue) && pathValue.ValueKind == JsonValueKind.String
				? pathValue.GetString() ?? string.Empty
				: string.Empty;
			line = root.TryGetProperty("line", out var lineValue) && lineValue.TryGetInt32(out int parsed)
				? parsed
				: 1;
		} catch (JsonException ex) {
			return CommandResult.Failure($"Could not open the file: invalid command arguments ({ex.Message}).");
		}

		if (path.Length == 0 || !Path.IsPathFullyQualified(path)) {
			return CommandResult.Failure($"Can't open '{path}': an absolute file path is required.");
		}

		session.FileOpener.Open(path, line, preview: false, scratch: false);
		return CommandResult.Success($"Opening {Path.GetFileName(path)}.");
	}
}
