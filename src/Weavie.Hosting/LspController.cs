using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Weavie.Core.Lsp;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

/// <summary>
/// One session's language-server multiplexer over its message bus. The page mints a <c>channel</c> per language
/// client and drives the LSP feature's <c>start</c>/<c>data</c>/<c>stop</c> events; this resolves the recipe, spawns the
/// server (one <see cref="LspChannel"/> per channel), and routes JSON-RPC both ways — so LSP rides whatever
/// transport the backend already has (in-process, WebSocket, or a future TLS-proxied one) and reaches remote
/// sessions, with no socket/port/token of its own. The successor to the per-session loopback bridge server.
/// </summary>
public sealed class LspController : IAsyncDisposable {
	private readonly string _workspaceRoot;
	private readonly ILspServerLauncher _launcher;
	private readonly Func<string, LanguageServerDescriptor?> _resolve;
	private readonly Action<string> _log;
	private readonly ConcurrentDictionary<ChannelKey, LspChannel> _channels = new();

	/// <summary>Creates the multiplexer for a session rooted at <paramref name="workspaceRoot"/>.</summary>
	/// <param name="workspaceRoot">The session's worktree, the working directory servers are spawned in.</param>
	/// <param name="launcher">Spawns a resolved server (the process seam; a fake in tests).</param>
	/// <param name="resolve">Maps a start selector to a server recipe (the catalog in production).</param>
	/// <param name="log">Diagnostic sink (server stderr + lifecycle).</param>
	public LspController(
		string workspaceRoot,
		ILspServerLauncher launcher,
		Func<string, LanguageServerDescriptor?> resolve,
		Action<string> log) {
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentNullException.ThrowIfNull(launcher);
		ArgumentNullException.ThrowIfNull(resolve);
		ArgumentNullException.ThrowIfNull(log);
		_workspaceRoot = workspaceRoot;
		_launcher = launcher;
		_resolve = resolve;
		_log = log;
	}

	/// <summary>
	/// Starts a server for <paramref name="server"/> bound to <paramref name="channel"/>. An unknown recipe, a
	/// server not on <c>PATH</c>, or a duplicate channel returns false with <paramref name="error"/>.
	/// </summary>
	internal bool Start(MessagePeer owner, string server, string channel, out string? error) {
		ArgumentNullException.ThrowIfNull(owner);
		if (string.IsNullOrEmpty(channel)) {
			error = "A language-server channel id is required.";
			return false;
		}

		var descriptor = string.IsNullOrEmpty(server) ? null : _resolve(server);
		if (descriptor is null) {
			error = $"no language server recipe for '{server}'";
			return false;
		}

		var command = ServerResolver.Resolve(descriptor);
		if (command is null) {
			string tried = string.Join(", ", descriptor.Candidates.Select(c => c.Command));
			error = $"{descriptor.DisplayName}: no language server on PATH (tried {tried})";
			return false;
		}

		var key = new ChannelKey(owner, channel);
		var ch = new LspChannel(
			owner.Target.Feature("lsp"),
			channel,
			command,
			_workspaceRoot,
			_launcher,
			_log,
			() => _channels.TryRemove(key, out _));
		if (!_channels.TryAdd(key, ch)) {
			ch.Dispose();
			error = $"channel '{channel}' is already bound to a live server";
			return false;
		}

		ch.Start();
		error = null;
		return true;
	}

	/// <summary>Forwards one JSON-RPC payload from the page to <paramref name="channel"/>'s server.</summary>
	internal void Data(MessagePeer owner, string channel, ReadOnlyMemory<byte> payload) {
		if (_channels.TryGetValue(new ChannelKey(owner, channel), out var ch)) {
			ch.Write(payload);
		}
	}

	/// <summary>Tears a channel's server down off the message-dispatch thread.</summary>
	internal Task StopAsync(MessagePeer owner, string channel) =>
		_channels.TryRemove(new ChannelKey(owner, channel), out var ch)
			? Task.Run(ch.Dispose)
			: Task.CompletedTask;

	internal Task DisconnectAsync(MessagePeer owner) {
		ArgumentNullException.ThrowIfNull(owner);
		var removed = new List<LspChannel>();
		foreach (var entry in _channels) {
			if (!ReferenceEquals(entry.Key.Owner, owner)
				|| !_channels.TryRemove(entry.Key, out var channel)) {
				continue;
			}

			removed.Add(channel);
		}

		return DisposeAsync(removed, publishExit: false, string.Empty);
	}

	/// <summary>
	/// Disposes every channel minted by a different page instance (channel ids end in <c>-{epoch}</c>). A fresh
	/// page owns no channels and sends no <c>lsp/stop</c> for a predecessor's, so a reload would otherwise leak
	/// one live server per language until the session unloads.
	/// </summary>
	internal Task DropOtherEpochsAsync(MessagePeer owner, string epoch) {
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentException.ThrowIfNullOrEmpty(epoch);
		string suffix = "-" + epoch;
		var removed = new List<LspChannel>();
		foreach (var entry in _channels) {
			if (!ReferenceEquals(entry.Key.Owner, owner)
				|| entry.Key.Channel.EndsWith(suffix, StringComparison.Ordinal)
				|| !_channels.TryRemove(entry.Key, out var channel)) {
				continue;
			}

			removed.Add(channel);
		}

		// The reaped channels' owners did not ask for this: publish exit after disposal so still-live sibling
		// clients tear down and reconnect instead of waiting on dead channels.
		return DisposeAsync(removed, publishExit: true, "superseded by a newer page instance");
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

	private static Task DisposeAsync(
		IReadOnlyList<LspChannel> channels,
		bool publishExit,
		string reason) =>
		channels.Count == 0
			? Task.CompletedTask
			: Task.Run(() => {
				foreach (var channel in channels) {
					if (publishExit) {
						channel.DisposeWithExit(reason);
					} else {
						channel.Dispose();
					}
				}
			});

	private readonly record struct ChannelKey(MessagePeer Owner, string Channel);
}
