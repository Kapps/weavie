using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

/// <summary>Sends notifications and requests from a native ACP agent to its client.</summary>
public sealed class AcpAgentConnection {
	private readonly TextWriter _output;
	private readonly Lock _writeGate;
	private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending;
	private readonly ConcurrentDictionary<long, byte> _cancelled;
	private long _nextRequestId;

	internal AcpAgentConnection(
		TextWriter output,
		Lock writeGate,
		ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending,
		ConcurrentDictionary<long, byte> cancelled) {
		_output = output;
		_writeGate = writeGate;
		_pending = pending;
		_cancelled = cancelled;
	}

	/// <summary>Sends an ACP notification.</summary>
	public void Notify(string method, JsonNode parameters) {
		ArgumentException.ThrowIfNullOrEmpty(method);
		ArgumentNullException.ThrowIfNull(parameters);
		Write(new JsonObject {
			["jsonrpc"] = "2.0",
			["method"] = method,
			["params"] = parameters,
		});
	}

	/// <summary>Sends an ACP client request and awaits its response.</summary>
	public async Task<JsonElement> RequestAsync(string method, JsonNode parameters, CancellationToken ct) {
		ArgumentException.ThrowIfNullOrEmpty(method);
		ArgumentNullException.ThrowIfNull(parameters);
		ct.ThrowIfCancellationRequested();
		long id = Interlocked.Increment(ref _nextRequestId);
		var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (!_pending.TryAdd(id, completion)) {
			throw new InvalidOperationException($"ACP client request id {id} is already pending.");
		}
		try {
			Write(new JsonObject {
				["jsonrpc"] = "2.0",
				["id"] = id,
				["method"] = method,
				["params"] = parameters,
			});
		} catch {
			_pending.TryRemove(id, out _);
			throw;
		}
		using var registration = ct.Register(() => Cancel(id, ct));
		return await completion.Task.ConfigureAwait(false);
	}

	internal void Write(JsonNode message) {
		string line = message.ToJsonString();
		lock (_writeGate) {
			_output.WriteLine(line);
			_output.Flush();
		}
	}

	private void Cancel(long id, CancellationToken ct) {
		if (!_pending.TryRemove(id, out var completion)) {
			return;
		}
		_cancelled.TryAdd(id, 0);
		Notify("$/cancel_request", new JsonObject { ["requestId"] = id });
		completion.TrySetCanceled(ct);
	}
}
