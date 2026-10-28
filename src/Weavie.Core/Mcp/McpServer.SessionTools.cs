using System.Text.Json;
using Weavie.Core.Json;
using Weavie.Core.Sessions;

namespace Weavie.Core.Mcp;

// currentSession / listSessions / readSession / messageSession: one session's view of the others on this host.
// See docs/specs/session-messaging.md.
public sealed partial class McpServer {
	private async Task HandleListSessionsAsync(IMcpResponder responder, string? idRaw, CancellationToken ct) {
		var peers = await _sessions.ListAsync(ct).ConfigureAwait(false);
		await SendToolTextAsync(responder, idRaw, JsonWrite.Object(writer => {
			writer.WriteString("self", _sessions.SelfId);
			writer.WriteStartArray("sessions");
			foreach (var peer in peers) {
				writer.WriteStartObject();
				writer.WriteString("id", peer.Id);
				writer.WriteString("title", peer.Title);
				writer.WriteString("provider", peer.ProviderId);
				writer.WriteBoolean("loaded", peer.Loaded);
				writer.WriteString("status", peer.Status);
				writer.WriteString("receive", JsonNamingPolicy.CamelCase.ConvertName(peer.Receive.ToString()));
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
		}), ct).ConfigureAwait(false);
	}

	private async Task HandleReadSessionAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		string id = args.GetStringOrEmpty("id");
		if (id.Length == 0 || !args.TryGetProperty("count", out var countValue)
			|| countValue.ValueKind != JsonValueKind.Number || !countValue.TryGetInt32(out int count) || count < 1) {
			await SendToolErrorAsync(responder, idRaw, "readSession requires an 'id' and a positive integer 'count'.", ct).ConfigureAwait(false);
			return;
		}

		SessionPeerTranscript transcript;
		try {
			transcript = await _sessions.ReadAsync(id, count, ct).ConfigureAwait(false);
		} catch (SessionPeerException ex) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}
		await SendToolTextAsync(responder, idRaw, JsonWrite.Object(writer => {
			writer.WriteString("id", transcript.Id);
			writer.WriteString("title", transcript.Title);
			writer.WriteNumber("total", transcript.Total);
			writer.WriteStartArray("entries");
			foreach (var entry in transcript.Entries) {
				writer.WriteStartObject();
				writer.WriteString("role", JsonNamingPolicy.CamelCase.ConvertName(entry.Role.ToString()));
				writer.WriteString("text", entry.Text);
				if (entry.Status is { } status) writer.WriteString("status", status);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
		}), ct).ConfigureAwait(false);
	}

	private async Task HandleMessageSessionAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		string id = args.GetStringOrEmpty("id");
		string message = args.GetStringOrEmpty("message");
		if (id.Length == 0 || message.Trim().Length == 0) {
			await SendToolErrorAsync(responder, idRaw, "messageSession requires an 'id' and a 'message'.", ct).ConfigureAwait(false);
			return;
		}

		SessionPeerDelivery delivery;
		try {
			delivery = await _sessions.SendAsync(id, message, ct).ConfigureAwait(false);
		} catch (SessionPeerException ex) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}
		await SendToolTextAsync(responder, idRaw,
			$"Delivered to '{delivery.Id}' (status: {delivery.Status}). It replies with messageSession if it has something to report; don't wait for it.",
			ct).ConfigureAwait(false);
	}
}
