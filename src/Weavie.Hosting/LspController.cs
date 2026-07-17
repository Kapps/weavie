using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Weavie.Core.Lsp;

namespace Weavie.Hosting;

/// <summary>
/// One session's language-server multiplexer over the web bridge. The page mints a <c>channel</c> per language
/// client and drives it with <c>lsp-start</c>/<c>lsp-data</c>/<c>lsp-stop</c>; this resolves the recipe, spawns the
/// server (one <see cref="LspChannel"/> per channel), and routes JSON-RPC both ways — so LSP rides whatever
/// transport the backend already has (in-process, WebSocket, or a future TLS-proxied one) and reaches remote
/// sessions, with no socket/port/token of its own. The successor to the per-session loopback bridge server.
/// </summary>
public sealed class LspController : IAsyncDisposable {
	private readonly IHostBridge _bridge;
	private readonly string _workspaceRoot;
	private readonly ILspServerLauncher _launcher;
	private readonly Func<string, LanguageServerDescriptor?> _resolve;
	private readonly Action<string> _log;
	private readonly ConcurrentDictionary<string, LspChannel> _channels = new(StringComparer.Ordinal);

	/// <summary>Creates the multiplexer for a session rooted at <paramref name="workspaceRoot"/>.</summary>
	/// <param name="bridge">The web bridge LSP frames ride.</param>
	/// <param name="workspaceRoot">The session's worktree, the working directory servers are spawned in.</param>
	/// <param name="launcher">Spawns a resolved server (the process seam; a fake in tests).</param>
	/// <param name="resolve">Maps an <c>lsp-start</c> selector to a server recipe (the catalog in production).</param>
	/// <param name="log">Diagnostic sink (server stderr + lifecycle).</param>
	public LspController(
		IHostBridge bridge,
		string workspaceRoot,
		ILspServerLauncher launcher,
		Func<string, LanguageServerDescriptor?> resolve,
		Action<string> log) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentNullException.ThrowIfNull(launcher);
		ArgumentNullException.ThrowIfNull(resolve);
		ArgumentNullException.ThrowIfNull(log);
		_bridge = bridge;
		_workspaceRoot = workspaceRoot;
		_launcher = launcher;
		_resolve = resolve;
		_log = log;
	}

	/// <summary>
	/// Raised when a start resolved its recipe but found no server installed — the exact moment the user opened
	/// a file of a supported language and got nothing, which triggers the install-offer suggestion.
	/// </summary>
	public event Action<LanguageServerDescriptor>? Unresolved;

	/// <summary>
	/// Starts a server for <paramref name="server"/> bound to <paramref name="channel"/> (tagging its output with
	/// <paramref name="slot"/>). An unknown recipe, a server not on <c>PATH</c>, or a channel id already bound to a
	/// live server replies <c>lsp-exit</c> with the reason instead of spawning — a duplicate id silently accepted
	/// would splice the new client onto the old server, whose second <c>initialize</c> faults.
	/// </summary>
	public void Start(string slot, string server, string channel) {
		if (string.IsNullOrEmpty(channel)) {
			return;
		}

		var descriptor = string.IsNullOrEmpty(server) ? null : _resolve(server);
		if (descriptor is null) {
			_bridge.PostToWeb(LspMessages.Exit(slot, channel, -1, $"no language server recipe for '{server}'"));
			return;
		}

		var command = ServerResolver.Resolve(descriptor);
		if (command is null) {
			string tried = string.Join(", ", descriptor.Candidates.Select(c => c.Command));
			_bridge.PostToWeb(LspMessages.Exit(slot, channel, -1, $"{descriptor.DisplayName}: no language server on PATH (tried {tried})"));
			Unresolved?.Invoke(descriptor);
			return;
		}

		var ch = new LspChannel(_bridge, slot, channel, command, _workspaceRoot, _launcher, _log, c => _channels.TryRemove(c, out _));
		if (!_channels.TryAdd(channel, ch)) {
			ch.Dispose();
			_bridge.PostToWeb(LspMessages.Exit(slot, channel, -1, $"channel '{channel}' is already bound to a live server"));
			return;
		}

		ch.Start();
	}

	/// <summary>Forwards one JSON-RPC payload from the page to <paramref name="channel"/>'s server.</summary>
	public void Data(string channel, ReadOnlyMemory<byte> payload) {
		if (_channels.TryGetValue(channel, out var ch)) {
			ch.Write(payload);
		}
	}

	/// <summary>Tears a channel's server down (off the bridge thread so a kill can't stall dispatch).</summary>
	public void Stop(string channel) {
		if (_channels.TryRemove(channel, out var ch)) {
			_ = Task.Run(ch.Dispose);
		}
	}

	/// <summary>
	/// Disposes every channel minted by a different page instance (channel ids end in <c>-{epoch}</c>). A fresh
	/// page owns no channels and sends no <c>lsp-stop</c> for a predecessor's, so a reload would otherwise leak
	/// one live server per language until the session unloads.
	/// </summary>
	public void DropOtherEpochs(string epoch) {
		ArgumentException.ThrowIfNullOrEmpty(epoch);
		string suffix = "-" + epoch;
		foreach (string channel in _channels.Keys) {
			if (channel.EndsWith(suffix, StringComparison.Ordinal) || !_channels.TryRemove(channel, out var ch)) {
				continue;
			}

			// The reaped channel's owner did not ask for this: post the exit (unlike a page-initiated Stop) so a
			// still-live sibling page's client tears down and reconnects instead of waiting on a dead channel.
			_ = Task.Run(() => ch.DisposeWithExit("superseded by a newer page instance"));
		}
	}

	/// <summary>
	/// Fans a debounced watcher batch to every live server as one <c>workspace/didChangeWatchedFiles</c>, so their
	/// diagnostics/types don't go stale after Claude edits on disk.
	/// </summary>
	public void NotifyWatchedFileChanges(IReadOnlyList<WatchedFileChange> changes) {
		if (changes.Count == 0 || _channels.IsEmpty) {
			return;
		}

		byte[] envelope = Encoding.UTF8.GetBytes(
			$"{{\"jsonrpc\":\"2.0\",\"method\":\"workspace/didChangeWatchedFiles\",\"params\":{DidChangeParams(changes)}}}");
		foreach (var ch in _channels.Values) {
			ch.Write(envelope);
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		var channels = _channels.Values.ToArray();
		_channels.Clear();
		// Reap off the calling (often UI) thread: each Dispose blocks until its server is killed + waited, so no
		// server outlives the session and a following worktree removal can't race a live process.
		await Task.Run(() => {
			foreach (var ch in channels) {
				ch.Dispose();
			}
		}).ConfigureAwait(false);
	}

	private static string DidChangeParams(IReadOnlyList<WatchedFileChange> changes) {
		var sb = new StringBuilder("{\"changes\":[");
		for (int i = 0; i < changes.Count; i++) {
			if (i > 0) {
				sb.Append(',');
			}

			sb.Append("{\"uri\":\"").Append(JsonEncodedText.Encode(changes[i].Uri))
				.Append("\",\"type\":").Append((int)changes[i].Kind).Append('}');
		}

		sb.Append("]}");
		return sb.ToString();
	}
}
