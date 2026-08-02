using System.Collections.Concurrent;
using System.Text.Json;

namespace Weavie.Hosting.Messaging;

internal sealed class MessageOperationRegistry {
	private readonly ConcurrentDictionary<long, MessageOperation> _active = new();
	private readonly Action<WebPeer, string> _sendToPeer;
	private readonly Action<string> _log;
	private readonly MessageExecutionPolicy _policy;
	private readonly TimeProvider _time;
	private long _sequence;
	private MessageOperationSnapshot? _lastFailure;

	public MessageOperationRegistry(
		Action<WebPeer, string> sendToPeer,
		Action<string> log,
		MessageExecutionPolicy policy,
		TimeProvider time) {
		ArgumentNullException.ThrowIfNull(sendToPeer);
		ArgumentNullException.ThrowIfNull(log);
		ArgumentNullException.ThrowIfNull(policy);
		ArgumentNullException.ThrowIfNull(time);
		policy.Validate();
		_sendToPeer = sendToPeer;
		_log = log;
		_policy = policy;
		_time = time;
	}

	public bool Healthy => Volatile.Read(ref _lastFailure) is null;

	public MessageOperation Start(
		WebPeer peer,
		MessageEnvelope envelope,
		Action<MessageOperation, string> timedOut) {
		ArgumentNullException.ThrowIfNull(envelope);
		ArgumentNullException.ThrowIfNull(timedOut);
		long sequence = Interlocked.Increment(ref _sequence);
		var operation = new MessageOperation(
			$"msg-{sequence}",
			peer,
			envelope,
			_policy,
			_time,
			OnSlow,
			(op, detail) => OnTimedOut(op, detail, timedOut),
			OnCompleted);
		if (!_active.TryAdd(sequence, operation)) {
			throw new InvalidOperationException($"Message operation sequence {sequence} is already active.");
		}

		operation.StartWatchdog();
		return operation;
	}

	public MessageHealthSnapshot Snapshot(bool ingressResponsive) => new(
		ingressResponsive && Healthy,
		ingressResponsive,
		[.. _active.Values
			.Select(operation => operation.Snapshot())
			.OrderBy(operation => operation.AcceptedAt)],
		Volatile.Read(ref _lastFailure));

	private void OnSlow(MessageOperation operation) {
		var snapshot = operation.Snapshot();
		RunDiagnostic(operation.Id, () => _log($"[message] slow {Describe(snapshot)}"));
		RunDiagnostic(operation.Id, () => operation.TryRunSlowDiagnostic(() =>
			SendNotification(
				operation,
				"busy",
				$"Still processing {snapshot.Endpoint} {snapshot.Feature}.{snapshot.Name} "
					+ $"({snapshot.Id}, stage {snapshot.Stage}, {snapshot.ElapsedMs} ms).",
				operation.NotificationKey)));
	}

	private void OnTimedOut(
		MessageOperation operation,
		string detail,
		Action<MessageOperation, string> timedOut) {
		var snapshot = operation.Snapshot();
		Volatile.Write(ref _lastFailure, snapshot);
		Remove(operation);
		timedOut(operation, detail);
		RunDiagnostic(operation.Id, () => _log($"[message] timed out {Describe(snapshot)}"));
		RunDiagnostic(operation.Id, () => operation.RunTerminalDiagnostic(() =>
			SendNotification(operation, "error", detail, operation.NotificationKey)));
	}

	private void OnCompleted(MessageOperation operation, bool wasSlow) {
		Remove(operation);
		if (wasSlow) {
			RunDiagnostic(operation.Id, () => operation.RunTerminalDiagnostic(() =>
				SendEvent(operation, "notifications", "clear", new { key = operation.NotificationKey })));
		}
	}

	private void RunDiagnostic(string operationId, Action diagnostic) => _ = Task.Run(() => {
		try {
			diagnostic();
		} catch (Exception ex) {
			SafeLog($"[message] diagnostic for {operationId} failed: {ex}");
		}
	});

	private void Remove(MessageOperation operation) {
		foreach (var entry in _active) {
			if (ReferenceEquals(entry.Value, operation)) {
				_active.TryRemove(entry.Key, out _);
				return;
			}
		}
	}

	private void SendNotification(MessageOperation operation, string level, string message, string key) =>
		SendEvent(operation, "notifications", "show", new { level, message, key });

	private void SendEvent(MessageOperation operation, string feature, string name, object payload) {
		try {
			_sendToPeer(
				operation.Peer,
				MessageEnvelope.Event(
					operation.Envelope.Scope,
					operation.Envelope.Session,
					feature,
					name,
					JsonSerializer.SerializeToElement(payload)).ToJson());
		} catch (Exception ex) {
			SafeLog($"[message] diagnostic delivery for {operation.Id} failed: {ex}");
		}
	}

	private void SafeLog(string message) {
		try {
			_log(message);
		} catch (Exception) {
			// A failed diagnostic sink cannot participate in message-operation control flow.
		}
	}

	private static string Describe(MessageOperationSnapshot snapshot) =>
		$"operation={snapshot.Id} endpoint={snapshot.Endpoint} peer={snapshot.Peer} "
		+ $"kind={snapshot.Kind} request={snapshot.RequestId ?? "-"} "
		+ $"handler={snapshot.Feature}.{snapshot.Name} stage={snapshot.Stage} elapsedMs={snapshot.ElapsedMs}";
}

internal sealed record MessageOperationSnapshot(
	string Id,
	string Endpoint,
	string Peer,
	string Kind,
	string? RequestId,
	string Feature,
	string Name,
	string Stage,
	DateTimeOffset AcceptedAt,
	long ElapsedMs);

internal sealed record MessageHealthSnapshot(
	bool Healthy,
	bool IngressResponsive,
	IReadOnlyList<MessageOperationSnapshot> ActiveOperations,
	MessageOperationSnapshot? LastFailure);
