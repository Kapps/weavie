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

	public static JsonElement HistoryPage(AgentPaneHistoryPage page) {
		ArgumentNullException.ThrowIfNull(page);
		return JsonSerializer.SerializeToElement(new {
			generation = page.Generation,
			restarted = page.Restarted,
			messages = page.Messages.Select(FragmentBody),
			cursor = page.Cursor is null ? null : new {
				generation = page.Cursor.Generation,
				ceiling = page.Cursor.Ceiling,
				before = page.Cursor.Before,
				jsonBefore = page.Cursor.JsonBefore,
				jsonRevision = page.Cursor.JsonRevision,
			},
		});
	}

	public static string Serialize(AgentPaneRecord record) => JsonSerializer.Serialize(Body(record));

	public static int Measure(AgentPaneFragment fragment) =>
		JsonSerializer.SerializeToUtf8Bytes(FragmentBody(fragment)).Length;

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
		turnId = record.Message.TurnId,
		startedAtMs = record.Message.StartedAtMs,
		itemId = record.Message.ItemId,
		itemType = record.Message.ItemType,
		category = record.Message.Category,
		summary = record.Message.Summary,
		text = record.Message.Text,
		status = record.Message.Status,
		questions = record.Message.Questions?.Select(question => new {
			id = question.Id,
			header = question.Header,
			question = question.Question,
			isSecret = question.IsSecret,
			options = question.Options.Select(option => new {
				label = option.Label,
				description = option.Description,
			}),
		}),
	};

	private static object FragmentBody(AgentPaneFragment fragment) => new {
		generation = fragment.Record.Generation,
		ordinal = fragment.Record.Ordinal,
		revision = fragment.Record.Revision,
		jsonOffset = fragment.JsonOffset,
		jsonLength = fragment.JsonLength,
		json = fragment.Json,
	};
}

internal sealed record AgentPaneRecord(
	long Generation,
	long Ordinal,
	long Revision,
	AgentPaneMessage Message);

internal sealed record AgentPaneFragment(
	AgentPaneRecord Record,
	string Json,
	int JsonOffset,
	int JsonLength);

internal sealed record AgentPaneHistoryCursor(
	long Generation,
	int Ceiling,
	int Before,
	int? JsonBefore,
	long? JsonRevision);

internal sealed record AgentPaneHistoryRequest(AgentPaneHistoryCursor? Cursor);

internal sealed record AgentPaneHistoryPage(
	long Generation,
	IReadOnlyList<AgentPaneFragment> Messages,
	AgentPaneHistoryCursor? Cursor,
	bool Restarted);
