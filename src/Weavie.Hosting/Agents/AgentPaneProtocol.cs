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

	public static object HistoryPage(AgentPaneHistoryPage page) {
		ArgumentNullException.ThrowIfNull(page);
		return new {
			readId = page.ReadId,
			generation = page.Generation,
			revision = page.Revision,
			messages = page.Messages.Select(FragmentBody),
			cursor = page.Cursor is null ? null : new {
				readId = page.Cursor.ReadId,
				before = page.Cursor.Before,
				jsonBefore = page.Cursor.JsonBefore,
			},
		};
	}

	public static string Serialize(AgentPaneRecord record) => JsonSerializer.Serialize(Body(record));

	public static int Measure(AgentPaneFragment fragment) {
		int bytes = MeasureEnvelope(fragment.Record, fragment.JsonOffset, fragment.JsonLength);
		foreach (char value in fragment.Json) {
			bytes += MeasureSerializedJsonCharacter(value);
		}
		return bytes;
	}

	internal static int MeasureEnvelope(AgentPaneRecord record, int jsonOffset, int jsonLength) =>
		FragmentSyntaxBytes
		+ NumberLength(record.Generation)
		+ NumberLength(record.Ordinal)
		+ NumberLength(record.Revision)
		+ NumberLength(jsonOffset)
		+ NumberLength(jsonLength);

	internal static int MeasureSerializedJsonCharacter(char value) => value switch {
		'"' => 6,
		'\\' => 2,
		>= ' ' and <= '\u007f' => 1,
		_ => throw new InvalidOperationException("Serialized agent history JSON must be ASCII and escaped."),
	};

	private static int NumberLength(long value) {
		if (value == 0) {
			return 1;
		}

		int length = value < 0 ? 1 : 0;
		ulong magnitude = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
		while (magnitude > 0) {
			length++;
			magnitude /= 10;
		}
		return length;
	}

	private static readonly int FragmentSyntaxBytes =
		"{\"generation\":".Length
		+ ",\"ordinal\":".Length
		+ ",\"revision\":".Length
		+ ",\"jsonOffset\":".Length
		+ ",\"jsonLength\":".Length
		+ ",\"json\":\"".Length
		+ "\"}".Length;

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
	string ReadId,
	int Before,
	int? JsonBefore);

internal sealed record AgentPaneHistoryRequest(
	AgentPaneHistoryCursor? Cursor,
	long? KnownGeneration,
	long? KnownRevision);

internal sealed record AgentPaneHistoryClose(string ReadId);

internal sealed record AgentPaneHistoryPage(
	string ReadId,
	long Generation,
	long Revision,
	IReadOnlyList<AgentPaneFragment> Messages,
	AgentPaneHistoryCursor? Cursor);
