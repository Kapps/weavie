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
/// The host half of the language-server bridge: a loopback WebSocket server that proxies each
/// authenticated connection to a spawned language server over stdio (the <c>vscode-ws-jsonrpc</c>
/// pattern). The WebView's <c>monaco-languageclient</c> connects to
/// <c>ws://127.0.0.1:{Port}/{selector}?token={token}</c>, where <c>selector</c> is an LSP language id
/// (or server id); the host resolves the matching <see cref="LanguageServerDescriptor"/>, finds the
/// server on <c>PATH</c> (<see cref="ServerResolver"/>), spawns it rooted at the workspace, and pipes
/// it. One subprocess per connection isolates failures. Mirrors the IDE-MCP server's loopback + token
/// posture: bind 127.0.0.1 only and require the per-session token on the upgrade.
/// </summary>
public sealed class LspBridgeServer : IAsyncDisposable {
	private readonly string _authToken;
	private readonly string _workspaceRoot;
	private readonly string? _allowedOrigin;
	private readonly Func<string, LanguageServerDescriptor?> _resolveDescriptor;
	private readonly ConcurrentDictionary<LspConnection, byte> _connections = new();
	private readonly IReadOnlySet<string> _watchedExtensions;
	private readonly Lock _watcherLock = new();

	private TcpListener? _listener;
	private CancellationTokenSource? _cts;
	private WorkspaceWatcher? _watcher;

	/// <summary>
	/// Creates the bridge. Call <see cref="Start"/> to begin listening.
	/// </summary>
	/// <param name="authToken">Per-session token required as the <c>token</c> query parameter on the upgrade.</param>
	/// <param name="workspaceRoot">Working directory the servers are spawned in (the project root).</param>
	/// <param name="allowedOrigin">If non-null, an <c>Origin</c> header, when present, must equal this.</param>
	/// <param name="resolveDescriptor">Maps a URL selector to a recipe; defaults to the built-in catalog.</param>
	public LspBridgeServer(
		string authToken,
		string workspaceRoot,
		string? allowedOrigin = "https://weavie.app",
		Func<string, LanguageServerDescriptor?>? resolveDescriptor = null) {
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
		_ = AcceptLoopAsync(_cts.Token);
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

			_ = HandleClientAsync(client, ct);
		}
	}

	private async Task HandleClientAsync(TcpClient client, CancellationToken ct) {
		using (client) {
			client.NoDelay = true;
			var stream = client.GetStream();
			try {
				var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
				if (request is null) {
					return;
				}

				var (target, headers) = request.Value;

				if (!headers.TryGetValue("sec-websocket-key", out string? wsKey)) {
					await WriteStatusAsync(stream, "400 Bad Request", ct).ConfigureAwait(false);
					return;
				}

				var (selector, token) = ParseTarget(target);

				if (!string.Equals(token, _authToken, StringComparison.Ordinal)) {
					Emit("rejected: missing/invalid token");
					await WriteStatusAsync(stream, "401 Unauthorized", ct).ConfigureAwait(false);
					return;
				}

				if (_allowedOrigin is not null
					&& headers.TryGetValue("origin", out string? origin)
					&& !string.Equals(origin, _allowedOrigin, StringComparison.Ordinal)) {
					Emit($"rejected: origin {origin}");
					await WriteStatusAsync(stream, "403 Forbidden", ct).ConfigureAwait(false);
					return;
				}

				var descriptor = string.IsNullOrEmpty(selector) ? null : _resolveDescriptor(selector);
				if (descriptor is null) {
					Emit($"no server recipe for selector '{selector}'");
					await WriteStatusAsync(stream, "404 Not Found", ct).ConfigureAwait(false);
					return;
				}

				var command = ServerResolver.Resolve(descriptor);
				if (command is null) {
					Emit($"{descriptor.DisplayName}: no server found on PATH (tried {string.Join(", ", descriptor.Candidates.Select(c => c.Command))})");
					await WriteStatusAsync(stream, "503 Service Unavailable", ct).ConfigureAwait(false);
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
		string accept = IdeLockFile.ComputeWebSocketAccept(wsKey);
		string response =
			"HTTP/1.1 101 Switching Protocols\r\n" +
			"Upgrade: websocket\r\n" +
			"Connection: Upgrade\r\n" +
			$"Sec-WebSocket-Accept: {accept}\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);

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

		// Track the live connection so the workspace watcher can forward didChangeWatchedFiles to it,
		// and lazily start watching once there's at least one server to notify (§9).
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

			_watcher = new WorkspaceWatcher(_workspaceRoot, _watchedExtensions, BroadcastFileChanges, Emit);
			_watcher.Start();
		}
	}

	// Forward a debounced batch of on-disk changes (incl. Claude/MCP edits) to every live server as a
	// single workspace/didChangeWatchedFiles notification, so diagnostics/types don't go stale (§9).
	private void BroadcastFileChanges(IReadOnlyList<WatchedFileChange> changes) {
		if (_connections.IsEmpty || changes.Count == 0) {
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

	private static async Task<(string Target, Dictionary<string, string> Headers)?> ReadRequestAsync(NetworkStream stream, CancellationToken ct) {
		var sb = new StringBuilder();
		byte[] one = new byte[1];
		int matched = 0; // counts the "\r\n\r\n" terminator
		while (matched < 4) {
			int n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
			if (n == 0) {
				return null;
			}

			char c = (char)one[0];
			sb.Append(c);
			matched = c switch {
				'\r' when matched is 0 or 2 => matched + 1,
				'\n' when matched is 1 or 3 => matched + 1,
				_ => 0,
			};

			if (sb.Length > 64 * 1024) {
				return null; // header flood guard
			}
		}

		string[] lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length == 0) {
			return null;
		}

		string[] requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
		string target = requestParts.Length >= 2 ? requestParts[1] : "/";

		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string? line in lines.Skip(1)) {
			int colon = line.IndexOf(':', StringComparison.Ordinal);
			if (colon > 0) {
				headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
			}
		}

		return (target, headers);
	}

	private static async Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken ct) {
		string response = $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	private void Emit(string message) => Log?.Invoke(message);

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		if (_cts is not null) {
			await _cts.CancelAsync().ConfigureAwait(false);
			_cts.Dispose();
		}

		lock (_watcherLock) {
			_watcher?.Dispose();
			_watcher = null;
		}

		_listener?.Stop();
	}
}
