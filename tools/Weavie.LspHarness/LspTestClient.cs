using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Weavie.LspHarness;

/// <summary>
/// A minimal LSP/JSON-RPC client over the in-process LSP bridge — the deterministic stand-in for
/// <c>monaco-languageclient</c>. Outbound messages go to <c>sendFrame</c> (the host's <c>lsp-data</c> sink);
/// inbound server frames are pushed in via <see cref="Deliver"/>. Matches responses to requests by id,
/// auto-answers the server-initiated startup requests (<c>workspace/configuration</c>,
/// <c>client/registerCapability</c>, …), and collects <c>publishDiagnostics</c> for the harness to assert on.
/// </summary>
internal sealed class LspTestClient : IAsyncDisposable {
	private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
	private readonly ConcurrentDictionary<string, JsonElement> _diagnosticsByUri = new(StringComparer.Ordinal);
	private readonly List<(Func<JsonElement, bool> Match, TaskCompletionSource<JsonElement> Tcs)> _notificationWaiters = [];
	private readonly object _waitersLock = new();
	private readonly ConcurrentDictionary<string, byte> _registeredMethods = new(StringComparer.Ordinal);
	private readonly IReadOnlyList<string> _workspaceFolders;
	private readonly JsonNode? _defaultSettings;
	private readonly Action<string> _log;
	private readonly bool _debug;
	private readonly Action<ReadOnlyMemory<byte>> _sendFrame;
	// Inbound server frames, drained by a single consumer so they're dispatched in arrival order.
	private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
	private readonly CancellationTokenSource _cts = new();
	private int _nextId;
	private Task? _receiveLoop;

	public LspTestClient(IReadOnlyList<string> workspaceFolders, Action<string> log, bool debug, JsonNode? defaultSettings, Action<ReadOnlyMemory<byte>> sendFrame) {
		_workspaceFolders = workspaceFolders;
		_defaultSettings = defaultSettings;
		_log = log;
		_debug = debug;
		_sendFrame = sendFrame;
	}

	/// <summary>Methods the server registered dynamically via <c>client/registerCapability</c> (e.g. csharp-ls).</summary>
	public bool IsRegistered(string method) => _registeredMethods.ContainsKey(method);

	/// <summary>Begins draining inbound frames. Call once before issuing requests.</summary>
	public void Start() => _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);

	/// <summary>Pushes one server JSON-RPC payload (an <c>lsp-data</c> from the host) into the client.</summary>
	public void Deliver(ReadOnlyMemory<byte> payload) => _inbound.Writer.TryWrite(payload.ToArray());

	/// <summary>Sends a request and awaits its matching response, throwing on a JSON-RPC error.</summary>
	public async Task<JsonElement> RequestAsync(string method, JsonNode? parameters, CancellationToken ct) {
		int id = Interlocked.Increment(ref _nextId);
		var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = tcs;

		var envelope = new JsonObject {
			["jsonrpc"] = "2.0",
			["id"] = id,
			["method"] = method,
			["params"] = parameters,
		};
		await SendAsync(envelope, ct);

		using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
		return await tcs.Task;
	}

	/// <summary>Sends a notification (no response expected).</summary>
	public async Task NotifyAsync(string method, JsonNode? parameters, CancellationToken ct) {
		var envelope = new JsonObject {
			["jsonrpc"] = "2.0",
			["method"] = method,
			["params"] = parameters,
		};
		await SendAsync(envelope, ct);
	}

	/// <summary>
	/// Waits for a <c>publishDiagnostics</c> notification for <paramref name="uri"/>, returning its
	/// <c>diagnostics</c> array. Resolves immediately if one already arrived.
	/// </summary>
	public async Task<JsonElement> WaitForDiagnosticsAsync(string uri, TimeSpan timeout, CancellationToken ct) {
		// Servers emit their own canonical URI form, so match on the normalized local path, not the raw string.
		// Some servers (gopls) push an initial empty publishDiagnostics then the real one once the package
		// loads, so keep waiting for a non-empty report until the timeout, then return what we have.
		string target = NormalizeUri(uri);
		var stopwatch = Stopwatch.StartNew();
		while (true) {
			if (_diagnosticsByUri.TryGetValue(target, out var stored)) {
				var diagnostics = stored.GetProperty("diagnostics");
				if (diagnostics.GetArrayLength() > 0 || stopwatch.Elapsed >= timeout) {
					return diagnostics;
				}
			}

			var remaining = timeout - stopwatch.Elapsed;
			if (remaining <= TimeSpan.Zero) {
				break;
			}

			try {
				await WaitForNotificationAsync(
					"textDocument/publishDiagnostics",
					n => n.TryGetProperty("uri", out var u) && string.Equals(NormalizeUri(u.GetString()), target, StringComparison.Ordinal),
					remaining,
					ct);
			} catch (TimeoutException) {
				break;
			}
		}

		return _diagnosticsByUri.TryGetValue(target, out var last)
			? last.GetProperty("diagnostics")
			: throw new TimeoutException($"No textDocument/publishDiagnostics for {uri} within {timeout.TotalSeconds:0}s.");
	}

	private static string NormalizeUri(string? uri) {
		if (string.IsNullOrEmpty(uri)) {
			return string.Empty;
		}

		string path;
		try {
			path = new Uri(uri).LocalPath;
		} catch (UriFormatException) {
			return uri;
		}

		// On Windows, an encoded-drive file URI (file:///c%3A/..) yields LocalPath "/c:/.."; strip the leading
		// slash so the drive path is real. Avoid Path.GetFullPath — it throws on "/c:/..".
		if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':') {
			path = path[1..];
		}

		path = path.Replace('\\', '/');
		return OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;
	}

	private async Task<JsonElement> WaitForNotificationAsync(string method, Func<JsonElement, bool> paramsMatch, TimeSpan timeout, CancellationToken ct) {
		var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		bool Match(JsonElement msg) =>
			msg.TryGetProperty("method", out var m) && m.GetString() == method
			&& msg.TryGetProperty("params", out var p) && paramsMatch(p);

		lock (_waitersLock) {
			_notificationWaiters.Add((Match, tcs));
		}

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeoutCts.CancelAfter(timeout);
		using var reg = timeoutCts.Token.Register(() => tcs.TrySetException(new TimeoutException($"No {method} within {timeout.TotalSeconds:0}s.")));
		return await tcs.Task;
	}

	private async Task ReceiveLoopAsync(CancellationToken ct) {
		try {
			await foreach (byte[] frame in _inbound.Reader.ReadAllAsync(ct)) {
				await DispatchAsync(Encoding.UTF8.GetString(frame), ct);
			}
		} catch (Exception ex) when (ex is JsonException or InvalidOperationException or OperationCanceledException) {
			_log($"receive loop error: {ex.Message}");
		}
	}

	private async Task DispatchAsync(string json, CancellationToken ct) {
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement.Clone();
		// LSP request ids may be integer or string. Treat both as ids — else we never reply and the
		// server blocks forever.
		bool hasId = root.TryGetProperty("id", out var idEl)
			&& idEl.ValueKind is JsonValueKind.Number or JsonValueKind.String;
		bool hasMethod = root.TryGetProperty("method", out var methodEl);

		if (_debug) {
			string? m = hasMethod ? methodEl.GetString() : "(response)";
			string kind = hasMethod && hasId ? "req" : hasMethod ? "notif" : "resp";
			_log($"<- {kind} {m}{(hasMethod && m == "textDocument/publishDiagnostics" ? " " + root.GetProperty("params").GetProperty("uri").GetString() : string.Empty)}");
		}

		if (hasMethod && hasId) {
			await ReplyToServerRequestAsync(methodEl.GetString() ?? string.Empty, idEl, root, ct);
			return;
		}

		if (hasMethod) {
			HandleNotification(methodEl.GetString() ?? string.Empty, root);
			return;
		}

		// Responses to our own requests always carry the numeric ids we minted.
		if (hasId && idEl.ValueKind == JsonValueKind.Number && _pending.TryRemove(idEl.GetInt32(), out var tcs)) {
			if (root.TryGetProperty("error", out var error)) {
				tcs.TrySetException(new InvalidOperationException($"LSP error: {error.GetRawText()}"));
			} else {
				tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res : default);
			}
		}
	}

	private void HandleNotification(string method, JsonElement root) {
		if (method == "textDocument/publishDiagnostics" && root.TryGetProperty("params", out var p)
			&& p.TryGetProperty("uri", out var uri) && uri.GetString() is { } uriStr) {
			_diagnosticsByUri[NormalizeUri(uriStr)] = p;
		}

		List<(Func<JsonElement, bool> Match, TaskCompletionSource<JsonElement> Tcs)> matched = [];
		lock (_waitersLock) {
			if (_debug && method == "textDocument/publishDiagnostics") {
				_log($"matching {method} against {_notificationWaiters.Count} waiter(s)");
			}

			for (int i = _notificationWaiters.Count - 1; i >= 0; i--) {
				bool isMatch;
				try {
					isMatch = _notificationWaiters[i].Match(root);
				} catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or KeyNotFoundException) {
					_log($"waiter match threw: {ex.GetType().Name}: {ex.Message}");
					isMatch = false;
				}

				if (isMatch) {
					matched.Add(_notificationWaiters[i]);
					_notificationWaiters.RemoveAt(i);
				}
			}
		}

		foreach (var (_, tcs) in matched) {
			tcs.TrySetResult(root.GetProperty("params"));
		}
	}

	// Answer the requests servers make during startup: the ones that matter (configuration, workspace
	// folders) get real replies, the rest get null so startup proceeds.
	private async Task ReplyToServerRequestAsync(string method, JsonElement id, JsonElement root, CancellationToken ct) {
		if (method == "client/registerCapability"
			&& root.TryGetProperty("params", out var rp)
			&& rp.TryGetProperty("registrations", out var regs)
			&& regs.ValueKind == JsonValueKind.Array) {
			foreach (var reg in regs.EnumerateArray()) {
				if (reg.TryGetProperty("method", out var rm) && rm.GetString() is { } m) {
					_registeredMethods[m] = 1;
				}
			}
		}

		JsonNode? result = method switch {
			"workspace/configuration" => ConfigurationResponse(root),
			"workspace/workspaceFolders" => WorkspaceFoldersResponse(),
			_ => null,
		};

		var envelope = new JsonObject {
			["jsonrpc"] = "2.0",
			["id"] = JsonNode.Parse(id.GetRawText()), // echo the id verbatim (integer or string)
			["result"] = result,
		};
		await SendAsync(envelope, ct);
	}

	private JsonArray ConfigurationResponse(JsonElement root) {
		// Reply with the default settings (or empty) once per requested item, so servers that gate features
		// on configuration (gopls semantic tokens) get what they need.
		int count = root.TryGetProperty("params", out var p) && p.TryGetProperty("items", out var items)
			? items.GetArrayLength()
			: 1;
		var array = new JsonArray();
		for (int i = 0; i < count; i++) {
			array.Add(_defaultSettings?.DeepClone() ?? new JsonObject());
		}

		return array;
	}

	private JsonArray WorkspaceFoldersResponse() {
		var array = new JsonArray();
		int index = 0;
		foreach (string folder in _workspaceFolders) {
			array.Add(new JsonObject {
				["uri"] = new Uri(folder).AbsoluteUri,
				["name"] = $"folder{index++}",
			});
		}

		return array;
	}

	private Task SendAsync(JsonNode envelope, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		_sendFrame(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
		return Task.CompletedTask;
	}

	public async ValueTask DisposeAsync() {
		_inbound.Writer.TryComplete();
		await _cts.CancelAsync();
		if (_receiveLoop is not null) {
			try {
				await _receiveLoop;
			} catch (OperationCanceledException) {
				// expected on shutdown
			}
		}

		_cts.Dispose();
	}
}
