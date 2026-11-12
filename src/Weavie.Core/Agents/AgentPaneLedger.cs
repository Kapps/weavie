using System.Text;

namespace Weavie.Core.Agents;

/// <summary>One ledger row: its stable ordinal, last-change revision, and the message it holds or just received.</summary>
public readonly record struct AgentPaneLedgerEntry(long Ordinal, long Revision, AgentPaneMessage Message);

/// <summary>
/// Folds a pane message stream into display rows: an item's start, deltas, and completion share one row. Not
/// thread-safe; the owner serializes access.
/// </summary>
public sealed class AgentPaneLedger {
	private readonly List<AgentPaneMessage> _messages = [];
	private readonly List<long> _ordinals = [];
	private readonly List<long> _revisions = [];
	private readonly Dictionary<string, int> _itemIndexes = new(StringComparer.Ordinal);
	private readonly HashSet<string> _activeItems = new(StringComparer.Ordinal);
	private readonly Dictionary<int, DeltaBuffer> _deltaBuffers = [];
	private readonly Dictionary<(string Provider, string? Conversation), List<int>> _conversationRows = [];
	private readonly Dictionary<(string Provider, string? Conversation), string?> _parents = [];
	private long _nextOrdinal;

	/// <summary>Folds <paramref name="messages"/> into a fresh ledger.</summary>
	public static AgentPaneLedger Of(IEnumerable<AgentPaneMessage> messages) {
		ArgumentNullException.ThrowIfNull(messages);
		var ledger = new AgentPaneLedger();
		foreach (var message in messages) ledger.Store(message);
		return ledger;
	}

	/// <summary>The number of rows.</summary>
	public int Count => _messages.Count;

	/// <summary>The latest revision assigned to any row.</summary>
	public long Revision { get; private set; }

	/// <summary>Allocates a revision for state that changes beside the rows, such as a block's data.</summary>
	public long NextRevision() => ++Revision;

	/// <summary>The last-change revision of row <paramref name="index"/>.</summary>
	public long RevisionAt(int index) => _revisions[index];

	/// <summary>The row index holding <paramref name="ordinal"/>, or a negative value when none does.</summary>
	public int IndexOfOrdinal(long ordinal) => _ordinals.BinarySearch(ordinal);

	/// <summary>The row holding the item <paramref name="key"/> names, when any.</summary>
	public bool TryItemIndex(string key, out int index) => _itemIndexes.TryGetValue(key, out index);


	/// <summary>Materialized rows whose items have not completed or retracted.</summary>
	public IReadOnlyList<AgentPaneMessage> ActiveItems =>
		[.. _activeItems.Select(key => EntryAt(_itemIndexes[key]).Message)];

	/// <summary>Reads only the rows belonging to one conversation, without visiting other histories.</summary>
	public IEnumerable<AgentPaneMessage> ConversationRows(string providerId, string? conversationId, bool active) {
		if (!_conversationRows.TryGetValue((providerId, conversationId), out var indexes)) yield break;
		foreach (int index in indexes) {
			if (!active || AgentPaneIdentity.ItemKey(_messages[index]) is { } key && _activeItems.Contains(key)) {
				yield return EntryAt(index).Message;
			}
		}
	}

	/// <summary>The parent announced for a nested conversation, or null for a root-owned thread.</summary>
	public string? ParentConversation(string providerId, string? conversationId) => _parents.GetValueOrDefault((providerId, conversationId));

	/// <summary>Row <paramref name="index"/> with its streamed text folded in.</summary>
	public AgentPaneLedgerEntry EntryAt(int index) {
		var message = _deltaBuffers.TryGetValue(index, out var buffer)
			? buffer.Latest with { Text = buffer.Text.ToString() }
			: _messages[index];
		return new(_ordinals[index], _revisions[index], message);
	}

	/// <summary>Folds <paramref name="message"/> in and returns its row with the message as received.</summary>
	public AgentPaneLedgerEntry Store(AgentPaneMessage message) {
		ArgumentNullException.ThrowIfNull(message);
		string? key = AgentPaneIdentity.ItemKey(message);
		if (key is null) {
			return Append(message);
		}

		if (message.Type == "item-started") {
			_activeItems.Add(key);
			if (_itemIndexes.TryGetValue(key, out int startedIndex)) return Replace(startedIndex, message);
			_itemIndexes[key] = _messages.Count;
			return Append(message);
		}

		if (IsDelta(message)) {
			_activeItems.Add(key);
			if (!_itemIndexes.TryGetValue(key, out int deltaIndex)) {
				deltaIndex = _messages.Count;
				_itemIndexes[key] = deltaIndex;
				Append(message with { Text = null });
			}
			if (!_deltaBuffers.TryGetValue(deltaIndex, out var buffer)) {
				var initial = _messages[deltaIndex];
				buffer = new DeltaBuffer(initial.Type == "item-started" ? initial : message);
				if (buffer.Latest.Type == "item-started") buffer.Text.Append(buffer.Latest.Text);
				_deltaBuffers.Add(deltaIndex, buffer);
			}
			buffer.Latest = message with {
				MediaType = buffer.Latest.MediaType ?? message.MediaType,
				MediaData = buffer.Latest.MediaData ?? message.MediaData,
				ResourceUri = buffer.Latest.ResourceUri ?? message.ResourceUri,
				MessageId = buffer.Latest.MessageId ?? message.MessageId,
			};
			buffer.Text.Append(message.Text);
			_revisions[deltaIndex] = ++Revision;
			return new(_ordinals[deltaIndex], _revisions[deltaIndex], message);
		}

		if (message.Type is "item-completed" or "item-retracted") {
			_activeItems.Remove(key);
			if (_itemIndexes.TryGetValue(key, out int completedIndex)) return Replace(completedIndex, message);
			_itemIndexes[key] = _messages.Count;
			return Append(message);
		}

		if (!_itemIndexes.ContainsKey(key)) _itemIndexes[key] = _messages.Count;
		return Append(message);
	}

	/// <summary>Removes every row and restarts ordinals and revisions.</summary>
	public void Clear() {
		_messages.Clear();
		_ordinals.Clear();
		_revisions.Clear();
		_itemIndexes.Clear();
		_activeItems.Clear();
		_deltaBuffers.Clear();
		_conversationRows.Clear();
		_parents.Clear();
		_nextOrdinal = 0;
		Revision = 0;
	}

	private AgentPaneLedgerEntry Replace(int index, AgentPaneMessage message) {
		_deltaBuffers.Remove(index);
		_messages[index] = message;
		_revisions[index] = ++Revision;
		return new(_ordinals[index], _revisions[index], message);
	}

	private AgentPaneLedgerEntry Append(AgentPaneMessage message) {
		var scope = (message.ProviderId, message.ConversationId);
		if (message.Type == "subagent-started") _parents.TryAdd(scope, message.ParentItemId);
		if (!_conversationRows.TryGetValue(scope, out var rows)) _conversationRows.Add(scope, rows = []);
		rows.Add(_messages.Count);
		long ordinal = ++_nextOrdinal;
		long revision = ++Revision;
		_messages.Add(message);
		_ordinals.Add(ordinal);
		_revisions.Add(revision);
		return new(ordinal, revision, message);
	}

	private static bool IsDelta(AgentPaneMessage message) =>
		message.Type is "agent-message-delta" or "thought-message-delta" or "user-message-delta"
			or "plan-delta" or "command-output-delta";

	private sealed class DeltaBuffer(AgentPaneMessage latest) {
		public AgentPaneMessage Latest { get; set; } = latest;
		public StringBuilder Text { get; } = new();
	}
}
