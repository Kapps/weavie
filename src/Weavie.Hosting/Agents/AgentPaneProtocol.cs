using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

/// <summary>Builds provider-neutral native agent pane payloads.</summary>
internal static class AgentPaneProtocol {
	public static object Message(AgentPaneRecord record) {
		ArgumentNullException.ThrowIfNull(record);
		return Body(record);
	}

	/// <summary>Builds one coalesced live-update payload.</summary>
	public static object Batch(IReadOnlyList<AgentPaneRecord> messages) {
		ArgumentNullException.ThrowIfNull(messages);
		return new { messages = messages.Select(Body) };
	}

	internal static async Task WriteHistoryAsync(AgentPaneHistory history, Stream output, CancellationToken ct) {
		const int batchSize = 64;
		for (int end = history.Messages.Count; end > 0; end -= batchSize) {
			int start = Math.Max(0, end - batchSize);
			await WriteBatchAsync(history.Messages.Skip(start).Take(end - start), false).ConfigureAwait(false);
		}
		await WriteBatchAsync([], true).ConfigureAwait(false);

		async Task WriteBatchAsync(IEnumerable<AgentPaneRecord> records, bool complete) {
			await JsonSerializer.SerializeAsync(output, new {
				generation = history.Generation,
				revision = history.Revision,
				count = history.Messages.Count,
				messages = records.Select(Body),
				complete,
			}, cancellationToken: ct).ConfigureAwait(false);
			await output.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
			await output.FlushAsync(ct).ConfigureAwait(false);
		}
	}

	private static object Body(AgentPaneRecord record) => new {
		generation = record.Generation,
		ordinal = record.Ordinal,
		revision = record.Revision,
		textOffset = 0,
		textLength = record.Message.Text?.Length ?? 0,
		type = record.Message.Type,
		providerId = record.Message.ProviderId,
		threadId = record.Message.ThreadId,
		isPrimaryThread = record.Message.IsPrimaryThread,
		conversationId = record.Message.ConversationId,
		anchorTurnId = record.Message.AnchorTurnId,
		turnId = record.Message.TurnId,
		startedAtMs = record.Message.StartedAtMs,
		itemId = record.Message.ItemId,
		requestId = record.Message.RequestId,
		itemType = record.Message.ItemType,
		itemIds = record.Message.ItemIds,
		category = record.Message.Category,
		summary = record.Message.Summary,
		text = record.Message.Text,
		status = record.Message.Status,
		questions = record.Message.Questions?.Select(question => new {
			id = question.Id,
			header = question.Header,
			question = question.Question,
			allowsOther = question.AllowsOther,
			kind = question.Kind,
			required = question.Required,
			format = question.Format,
			initialValues = question.InitialValues,
			minimum = question.Minimum,
			maximum = question.Maximum,
			minimumLength = question.MinimumLength,
			maximumLength = question.MaximumLength,
			pattern = question.Pattern,
			options = question.Options.Select(option => new {
				value = option.Value,
				label = option.Label,
				description = option.Description,
			}),
		}),
		answers = record.Message.Answers,
		actions = record.Message.Actions?.Select(action => new {
			id = action.Id,
			label = action.Label,
			kind = action.Kind,
		}),
		locations = record.Message.Locations?.Select(location => new {
			path = location.Path,
			line = location.Line,
		}),
		diffs = record.Message.Diffs?.Select(diff => new {
			path = diff.Path,
		}),
		content = record.Message.Content?.Select(content => new {
			type = content.Type,
			text = content.Text,
			mediaType = content.MediaType,
			mediaData = content.MediaData,
			resourceUri = content.ResourceUri,
			name = content.Name,
		}),
		parentItemId = record.Message.ParentItemId,
		background = record.Message.Background,
		terminalId = record.Message.TerminalId,
		mediaType = record.Message.MediaType,
		mediaData = record.Message.MediaData,
		resourceUri = record.Message.ResourceUri,
	};

}

internal sealed record AgentPaneRecord(
	long Generation,
	long Ordinal,
	long Revision,
	AgentPaneMessage Message);

internal sealed record AgentPaneHistoryRequest(long? KnownGeneration, long? KnownRevision);

internal sealed record AgentPaneHistory(
	long Generation,
	long Revision,
	IReadOnlyList<AgentPaneRecord> Messages);
