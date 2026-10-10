using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Agents;

/// <summary>Builds provider-neutral native agent pane payloads.</summary>
internal static class AgentPaneProtocol {
	public static AgentPaneWire Message(AgentPaneRecord record) {
		ArgumentNullException.ThrowIfNull(record);
		return Body(record, outputDeferred: false);
	}

	/// <summary>Builds one coalesced live-update payload.</summary>
	public static AgentPaneBatch Batch(IReadOnlyList<AgentPaneRecord> messages) {
		ArgumentNullException.ThrowIfNull(messages);
		return new([.. messages.Select(Message)]);
	}

	internal static async Task WriteHistoryAsync(AgentPaneHistory history, Stream output, CancellationToken ct) {
		const int batchSize = 64;
		for (int end = history.Messages.Count; end > 0; end -= batchSize) {
			int start = Math.Max(0, end - batchSize);
			await WriteBatchAsync(history.Messages.Skip(start).Take(end - start), false).ConfigureAwait(false);
		}
		await WriteBatchAsync([], true).ConfigureAwait(false);

		async Task WriteBatchAsync(IEnumerable<AgentPaneRecord> records, bool complete) {
			await JsonSerializer.SerializeAsync(
				output,
				new AgentPaneHistoryBatch(
					history.Generation,
					history.Revision,
					history.Messages.Count,
					[.. records.Select(HistoryBody)],
					complete),
				WireJson.Default.AgentPaneHistoryBatch,
				ct).ConfigureAwait(false);
			await output.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
			await output.FlushAsync(ct).ConfigureAwait(false);
		}
	}

	// Completed tool output only renders once expanded, so history leaves it for the `toolOutput` request.
	private static AgentPaneWire HistoryBody(AgentPaneRecord record) =>
		record.Message is { Type: "item-completed", ItemType: "tool" } tool && (tool.Text is not null || tool.Content is { Count: > 0 })
			? Body(record with { Message = tool with { Text = null, Content = null } }, outputDeferred: true)
			: Body(record, outputDeferred: false);

	private static AgentPaneWire Body(AgentPaneRecord record, bool outputDeferred) {
		var message = record.Message;
		return new(
			record.Generation,
			record.Ordinal,
			record.Revision,
			outputDeferred,
			0,
			message.Text?.Length ?? 0,
			message.Type,
			message.ProviderId,
			message.ThreadId,
			message.IsPrimaryThread,
			message.ConversationId,
			message.AnchorTurnId,
			message.TurnId,
			message.StartedAtMs,
			message.CompletedAtMs,
			message.ItemId,
			message.RequestId,
			message.ItemType,
			message.ItemIds,
			message.Category,
			message.Summary,
			message.Text,
			message.Status,
			message.Questions,
			message.Answers,
			message.Actions,
			message.Locations,
			message.Diffs?.Select(diff => new AgentPaneDiffPath(diff.Path)).ToArray(),
			message.Content,
			message.ParentItemId,
			message.Background,
			message.TerminalId,
			message.MediaType,
			message.MediaData,
			message.ResourceUri);
	}
}

internal sealed record AgentPaneWire(
	long Generation,
	long Ordinal,
	long Revision,
	bool OutputDeferred,
	int TextOffset,
	int TextLength,
	string Type,
	string ProviderId,
	string? ThreadId,
	bool? IsPrimaryThread,
	string? ConversationId,
	string? AnchorTurnId,
	string? TurnId,
	long? StartedAtMs,
	long? CompletedAtMs,
	string? ItemId,
	string? RequestId,
	string? ItemType,
	IReadOnlyList<string>? ItemIds,
	string? Category,
	string? Summary,
	string? Text,
	string? Status,
	IReadOnlyList<AgentInputQuestion>? Questions,
	IReadOnlyDictionary<string, IReadOnlyList<string>>? Answers,
	IReadOnlyList<AgentActionOption>? Actions,
	IReadOnlyList<AgentPaneLocation>? Locations,
	IReadOnlyList<AgentPaneDiffPath>? Diffs,
	IReadOnlyList<AgentPaneContent>? Content,
	string? ParentItemId,
	bool? Background,
	string? TerminalId,
	string? MediaType,
	string? MediaData,
	string? ResourceUri);

internal sealed record AgentPaneDiffPath(string Path);

internal sealed record AgentPaneBatch(IReadOnlyList<AgentPaneWire> Messages);

internal sealed record AgentPaneHistoryBatch(
	long Generation,
	long Revision,
	int Count,
	IReadOnlyList<AgentPaneWire> Messages,
	bool Complete);

internal sealed record AgentPaneRecord(
	long Generation,
	long Ordinal,
	long Revision,
	AgentPaneMessage Message);

internal sealed record AgentPaneRecordRequest(long Generation, long Ordinal);

internal sealed record AgentPaneHistoryRequest(long? KnownGeneration, long? KnownRevision);

internal sealed record AgentPaneHistory(
	long Generation,
	long Revision,
	IReadOnlyList<AgentPaneRecord> Messages);
