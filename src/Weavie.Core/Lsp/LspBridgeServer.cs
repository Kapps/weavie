using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Mcp;

namespace Weavie.Core.Lsp;

/// <summary>
/// The host half of the language-server bridge: a loopback WebSocket server that proxies each authenticated
/// connection to a spawned language server over stdio (the <c>vscode-ws-jsonrpc</c> pattern). The WebView's
/// <c>monaco-languageclient</c> connects to <c>ws://127.0.0.1:{Port}/{selector}?token={token}</c>, where
/// <c>selector</c> is an LSP language id (or server id); the host resolves the matching
/// <see cref="LanguageServerDescriptor"/>, finds the server on <c>PATH</c> (<see cref="ServerResolver"/>),
/// spawns it rooted at the workspace, and pipes it. One subprocess per connection isolates failures. Binds
/// 127.0.0.1 only and requires the per-session token on the upgrade.
/// </summary>
public sealed class LspBridgeServer : IAsyncDisposable {
	private readonly string _authToken;
	private readonly string _workspaceRoot;
	private readonly string? _allowedOrigin;
	private readonly Func<string, LanguageServerDescriptor?> _resolveDescriptor;
	private readonly ConcurrentDictionary<LspConnection, byte> _connections = new();
	// Every in-flight client-handling task, tracked so DisposeAsync can await them and guarantee each spawned
	// server process is killed and reaped before it returns.
	private readonly ConcurrentDictionary<Task, byte> _clientTasks = new();
	private readonly IReadOnlySet<string> _watchedExtensions;
	private readonly Lock _watcherLock = new();

	private TcpListener? _listener;
	private CancellationTokenSource? _cts;
	private WorkspaceWatcher? _watcher;
	private Task? _acceptLoop;

	/// <summary>Creates the bridge. Call <see cref="Start"/> to begin listening.</summary>
	/// <param name="authToken">Per-session token required as the <c>token</c> query parameter on the upgrade.</param>
	/// <param name="workspaceRoot">Working directory the servers are spawned in (the project root).</param>
	/// <param name="allowedOrigin">If non-null, an <c>Origin</c> header, when present, must equal this.</param>
	/// <param name="resolveDescriptor">Maps a URL selector to a recipe; defaults to the built-in catalog.</param>
	public LspBridgeServer(
		string authToken,
		string workspaceRoot,
		string? allowedOrigin,
		Func<string, LanguageServerDescriptor?>? resolveDescriptor) {
		ArgumentException.ThrowIfNullOrEmpty(authToken);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);

		_authToken = authToken;
		_workspaceRoot = workspaceRoot;
		_allowedOrigin = allowedOrigin;
		_resolveDescriptor = resolveDescriptor ?? DefaultResolve;
		_watchedExtensions = LanguageServerCatalog.All
			.SelectMany(d => d.FileExtensions)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>Diagnostic log line (connections, spawns, server stderr, teardown).</summary>
	public event Action<string>? Log;

	/// <summary>
	/// Raised with each debounced batch of on-disk changes the workspace watcher reports (the same batch sent to
	/// the language servers), so the host can also forward them to the editor's <c>file://</c> provider in the
	/// page. Fires whenever the watcher is running, even with no LSP server connected. Invoked off the UI thread.
	/// </summary>
	public event Action<IReadOnlyList<WatchedFileChange>>? FileChanges;

	/// <summary>The loopback port the server is listening on; 0 until <see cref="Start"/> is called.</summary>
	public int Port { get; private set; }

	/// <summary>Binds an ephemeral loopback port, starts accepting, and returns the port.</summary>
	public int Start() {
		if (_listener is not null) {
			throw new InvalidOperationException("Server already started.");
		}

		_listener = new TcpListener(IPAddress.Loopback, 0);
		_listener.Start();
		Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
		_cts = new CancellationTokenSource();
		_acceptLoop = AcceptLoopAsync(_cts.Token);
		Emit($"listening on 127.0.0.1:{Port}; workspace {_workspaceRoot}");
		return Port;
	}

	private static LanguageServerDescriptor? DefaultResolve(string selector) =>
		LanguageServerCatalog.ForLanguage(selector) ?? LanguageServerCatalog.ForServerId(selector);

	private async Task AcceptLoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			TcpClient client;
			try {
				client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
			} catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) {
				break;
			}

			TrackClient(HandleClientAsync(client, ct));
		}
	}

	// Hold a reference to each client task so DisposeAsync can await it; drop it once it finishes.
	private void TrackClient(Task task) {
		_clientTasks[task] = 1;
		_ = task.ContinueWith(t => _clientTasks.TryRemove(t, out _), TaskScheduler.Default);
	}

	private async Task HandleClientAsync(TcpClient client, CancellationToken ct) {
		using (client) {
			client.NoDelay = true;
			var stream = client.GetStream();
			try {
				var request = await WebSocketHandshake.ReadRequestAsync(stream, ct).ConfigureAwait(false);
				if (request is null) {
					return;
				}

				var (target, headers) = request.Value;

				if (!headers.TryGetValue("sec-websocket-key", out string? wsKey)) {
					await WebSocketHandshake.WriteStatusAsync(stream, "400 Bad Request", ct).ConfigureAwait(false);
					return;
				}

				var (selector, token) = ParseTarget(target);

				if (!string.Equals(token, _authToken, StringComparison.Ordinal)) {
					Emit("rejected: missing/invalid token");
					await WebSocketHandshake.WriteStatusAsync(stream, "401 Unauthorized", ct).ConfigureAwait(false);
					return;
				}

				if (_allowedOrigin is not null
					&& headers.TryGetValue("origin", out string? origin)
					&& !string.Equals(origin, _allowedOrigin, StringComparison.Ordinal)) {
					Emit($"rejected: origin {origin}");
					await WebSocketHandshake.WriteStatusAsync(stream, "403 Forbidden", ct).ConfigureAwait(false);
					return;
				}

				var descriptor = string.IsNullOrEmpty(selector) ? null : _resolveDescriptor(selector);
				if (descriptor is null) {
					Emit($"no server recipe for selector '{selector}'");
					await WebSocketHandshake.WriteStatusAsync(stream, "404 Not Found", ct).ConfigureAwait(false);
					return;
				}

				var command = ServerResolver.Resolve(descriptor);
				if (command is null) {
					Emit($"{descriptor.DisplayName}: no server found on PATH (tried {string.Join(", ", descriptor.Candidates.Select(c => c.Command))})");
					await WebSocketHandshake.WriteStatusAsync(stream, "503 Service Unavailable", ct).ConfigureAwait(false);
					return;
				}

				await UpgradeAndProxyAsync(stream, wsKey, descriptor, command, ct).ConfigureAwait(false);
			} catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException) {
				Emit($"client error: {ex.GetType().Name}");
			}
		}
	}

	private async Task UpgradeAndProxyAsync(
		NetworkStream stream, string wsKey, LanguageServerDescriptor descriptor, ResolvedCommand command, CancellationToken ct) {
		await WebSocketHandshake.WriteUpgradeAsync(stream, wsKey, ct).ConfigureAwait(false);

		Process process;
		try {
			process = StartServer(command);
		} catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
			Emit($"{descriptor.DisplayName}: failed to start {command.ServerPath}: {ex.Message}");
			return;
		}

		Emit($"{descriptor.DisplayName}: spawned {command.ServerPath} (pid {process.Id})");
		using var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
		var connection = new LspConnection(ws, process, descriptor.DisplayName, Emit);

		// Track the live connection so the watcher can forward didChangeWatchedFiles to it, and lazily start
		// watching once there's at least one server to notify (§9).
		_connections[connection] = 1;
		EnsureWatcherStarted();
		try {
			await connection.RunAsync(ct).ConfigureAwait(false);
		} finally {
			_connections.TryRemove(connection, out _);
		}
	}

	private void EnsureWatcherStarted() {
		lock (_watcherLock) {
			if (_watcher is not null) {
				return;
			}

			_watcher = new WorkspaceWatcher(_workspaceRoot, _watchedExtensions, BroadcastFileChanges, Emit, debounceMs: 250);
			_watcher.Start();
		}
	}

	// Forward a debounced batch of on-disk changes to every live server as one
	// workspace/didChangeWatchedFiles notification so diagnostics/types don't go stale (§9), and mirror the
	// same batch to the page's file:// provider via FileChanges (independent of LSP connection state).
	private void BroadcastFileChanges(IReadOnlyList<WatchedFileChange> changes) {
		if (changes.Count == 0) {
			return;
		}

		FileChanges?.Invoke(changes);
		if (_connections.IsEmpty) {
			return;
		}

		var builder = new StringBuilder("{\"changes\":[");
		for (int i = 0; i < changes.Count; i++) {
			if (i > 0) {
				builder.Append(',');
			}

			builder.Append("{\"uri\":\"").Append(JsonEncodedText.Encode(changes[i].Uri))
				.Append("\",\"type\":").Append((int)changes[i].Kind).Append('}');
		}

		builder.Append("]}");
		string paramsJson = builder.ToString();
		var token = _cts?.Token ?? CancellationToken.None;
		Emit($"didChangeWatchedFiles: {changes.Count} change(s) → {_connections.Count} server(s)");
		foreach (var connection in _connections.Keys) {
			_ = connection.SendNotificationAsync("workspace/didChangeWatchedFiles", paramsJson, token);
		}
	}

	private Process StartServer(ResolvedCommand command) {
		var psi = new ProcessStartInfo {
			FileName = command.FileName,
			WorkingDirectory = _workspaceRoot,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardErrorEncoding = Encoding.UTF8,
		};
		foreach (string arg in command.Arguments) {
			psi.ArgumentList.Add(arg);
		}

		var process = new Process { StartInfo = psi };
		process.Start();
		return process;
	}

	private static (string Selector, string? Token) ParseTarget(string target) {
		int queryIndex = target.IndexOf('?', StringComparison.Ordinal);
		string path = queryIndex >= 0 ? target[..queryIndex] : target;
		string query = queryIndex >= 0 ? target[(queryIndex + 1)..] : string.Empty;
		string selector = Uri.UnescapeDataString(path.Trim('/'));
		return (selector, ParseQueryValue(query, "token"));
	}

	private static string? ParseQueryValue(string query, string key) {
		foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			int eq = pair.IndexOf('=', StringComparison.Ordinal);
			if (eq <= 0) {
				continue;
			}

			if (string.Equals(pair[..eq], key, StringComparison.Ordinal)) {
				return Uri.UnescapeDataString(pair[(eq + 1)..]);
			}
		}

		return null;
	}

	private void Emit(string message) => Log?.Invoke(message);

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		// Stop accepting, then cancel: in-flight connections unwind their pumps and kill+reap their spawned
		// server process (whole process tree). Await the accept loop and every client task so no server is
		// still alive (and no handle is still held on the workspace directory) by the time this returns, so a
		// session's worktree can be removed right after disposal without racing a still-running server (on
		// Windows a live cwd/handle makes `git worktree remove` fail with "Directory not empty").
		_listener?.Stop();
		if (_cts is not null) {
			await _cts.CancelAsync().ConfigureAwait(false);
		}

		try {
			if (_acceptLoop is not null) {
				await _acceptLoop.ConfigureAwait(false);
			}

			await Task.WhenAll([.. _clientTasks.Keys]).ConfigureAwait(false);
		} catch (Exception ex) {
			// Best-effort teardown, but kept observable: a connection that faulted on its way down is logged,
			// never swallowed silently or rethrown out of dispose.
			Emit($"teardown error draining connections: {ex.Message}");
		}

		_cts?.Dispose();

		lock (_watcherLock) {
			_watcher?.Dispose();
			_watcher = null;
		}
	}
}
