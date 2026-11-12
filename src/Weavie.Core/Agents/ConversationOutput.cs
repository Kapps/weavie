namespace Weavie.Core.Agents;

/// <summary>Enqueues ordered output without calling conversation controllers or UI observers.</summary>
public interface IConversationOutputSink {
	/// <summary>Enqueues one committed revision.</summary>
	void Live(AgentPaneRecord record);
	/// <summary>Enqueues replacement of the visible history.</summary>
	void Reset();
	/// <summary>Enqueues a block's latest state.</summary>
	void Block(AgentBlockRecord record);
}

/// <summary>The session's single output ledger, durable-write boundary, and ordered publication source.</summary>
public sealed class ConversationOutput(IConversationOutputSink sink) {
	private readonly Lock _gate = new();
	private readonly AgentPaneLedger _ledger = new();
	private readonly Dictionary<(string ProviderId, string? ConversationId, string BlockId), AgentBlockRecord> _blocks = [];
	private long _generation;

	/// <summary>Binds a read view to one provider and Weavie conversation scope.</summary>
	public ConversationView View(string providerId, string? conversationId) => new(this, providerId, conversationId);

	/// <summary>Persists and folds an event before enqueuing its revision.</summary>
	public void Write(AgentPaneMessage message, Action persist) {
		lock (_gate) {
			persist();
			if (message.Type == "transcript-reset") {
				Reset();
				sink.Reset();
			} else {
				// A block lives while its reference row does.
				if (message.Type == "item-retracted" && AgentPaneIdentity.ItemKey(message) is { } key
					&& _ledger.TryItemIndex(key, out int index) && _ledger.EntryAt(index).Message is var row && AgentBlockReferences.IsBlockRow(row)) {
					_blocks.Remove((row.ProviderId, row.ConversationId, row.BlockId!));
				}
				sink.Live(Record(_ledger.Store(message)));
			}
		}
	}

	/// <summary>Persists a block's latest state and its reference row, then enqueues both.</summary>
	public void WriteBlock(AgentBlockState state, AgentPaneMessage row, Action persist) {
		ArgumentNullException.ThrowIfNull(state);
		lock (_gate) {
			persist();
			sink.Live(Record(_ledger.Store(row)));
			sink.Block(SetBlock(state));
		}
	}

	/// <summary>Loads saved history and the blocks it shows (the caller passes only those) before the session is addressable.</summary>
	public void Restore(IReadOnlyList<AgentPaneMessage> messages, IReadOnlyList<AgentBlockState> blocks) => Replace(messages, blocks, publish: false);

	/// <summary>Replaces history and its blocks and publishes the new generation.</summary>
	public void Replace(IReadOnlyList<AgentPaneMessage> messages, IReadOnlyList<AgentBlockState> blocks) => Replace(messages, blocks, publish: true);

	private void Replace(IReadOnlyList<AgentPaneMessage> messages, IReadOnlyList<AgentBlockState> blocks, bool publish) {
		lock (_gate) {
			if (_ledger.Count > 0 || _blocks.Count > 0) {
				Reset();
				if (publish) sink.Reset();
			}
			foreach (var message in messages) {
				var record = Record(_ledger.Store(message));
				if (publish) sink.Live(record);
			}
			foreach (var block in blocks) {
				var record = SetBlock(block);
				if (publish) sink.Block(record);
			}
		}
	}

	/// <summary>The current state of block <paramref name="blockId"/> in one conversation.</summary>
	public AgentBlock? Block(string providerId, string? conversationId, string blockId) {
		lock (_gate) return _blocks.GetValueOrDefault((providerId, conversationId, blockId))?.State.Block;
	}

	/// <summary>Returns a consistent full snapshot or changes since the client's baseline.</summary>
	public AgentPaneHistory ReadHistory(long? knownGeneration, long? knownRevision) {
		lock (_gate) {
			if ((knownGeneration is null) != (knownRevision is null)
				|| knownGeneration == _generation && knownRevision > _ledger.Revision) {
				throw new ArgumentException("The agent transcript history baseline is invalid.");
			}
			long? after = knownGeneration == _generation ? knownRevision : null;
			return new(_generation, _ledger.Revision, [.. Enumerable.Range(0, _ledger.Count)
				.Where(index => after is null || _ledger.RevisionAt(index) > after)
				.Select(index => Record(_ledger.EntryAt(index)))],
				[.. _blocks.Values.Where(block => after is null || block.Revision > after)]);
		}
	}

	/// <summary>Reads the exact record addressed by a client.</summary>
	public AgentPaneRecord ReadRecord(long generation, long ordinal) {
		lock (_gate) {
			int index = _ledger.IndexOfOrdinal(ordinal);
			if (generation != _generation || index < 0) throw new InvalidOperationException("The agent transcript record no longer exists.");
			return Record(_ledger.EntryAt(index));
		}
	}

	/// <summary>Returns the materialized item when its complete identity exists.</summary>
	public AgentPaneMessage? FindItem(string key) {
		lock (_gate) return _ledger.TryItemIndex(key, out int index) ? _ledger.EntryAt(index).Message : null;
	}

	internal IReadOnlyList<AgentPaneMessage> ReadView(string providerId, string? conversationId, bool active) {
		lock (_gate) return [.. _ledger.ConversationRows(providerId, conversationId, active)];
	}

	internal string? ParentConversation(string providerId, string? conversationId) {
		lock (_gate) return _ledger.ParentConversation(providerId, conversationId);
	}

	private AgentPaneRecord Record(AgentPaneLedgerEntry entry) => new(_generation, entry.Ordinal, entry.Revision, entry.Message);

	private AgentBlockRecord SetBlock(AgentBlockState state) =>
		_blocks[AgentBlockReferences.Key(state)] = new(_generation, _ledger.NextRevision(), state);

	private void Reset() {
		_generation++;
		_ledger.Clear();
		_blocks.Clear();
	}
}

/// <summary>A passive view of a conversation's rows; the controller owns its execution.</summary>
public sealed class ConversationView(ConversationOutput output, string providerId, string? conversationId) {
	/// <summary>The announced parent scope, when this is a provider-created child.</summary>
	public string? ParentConversationId => output.ParentConversation(providerId, conversationId);

	/// <summary>The current materialized transcript.</summary>
	public IReadOnlyList<AgentPaneMessage> Rows => output.ReadView(providerId, conversationId, active: false);

	/// <summary>This conversation's current block <paramref name="blockId"/>.</summary>
	public AgentBlock? Block(string blockId) => output.Block(providerId, conversationId, blockId);

	/// <summary>The block whose reference row <paramref name="retraction"/> removes, when it removes one.</summary>
	public string? RetractedBlock(AgentPaneMessage retraction) =>
		retraction.Type == "item-retracted" && AgentPaneIdentity.ItemKey(retraction) is { } key
			&& output.FindItem(key) is { } row && AgentBlockReferences.IsBlockRow(row) ? row.BlockId : null;

	/// <summary>Materializes the exact content run for its controller to complete and persist.</summary>
	public AgentPaneMessage? CompleteContent(string? threadId, string turnId, string itemId) {
		string key = AgentPaneIdentity.ItemKey(providerId, conversationId, threadId, turnId, itemId)!;
		var row = output.FindItem(key);
		return row is null ? null : row with { Type = "item-completed", Status = "completed" };
	}

}

/// <summary>A materialized row or live update at an exact generation, ordinal, and revision.</summary>
public sealed record AgentPaneRecord(long Generation, long Ordinal, long Revision, AgentPaneMessage Message);

/// <summary>A consistent full or incremental snapshot of a session's output.</summary>
public sealed record AgentPaneHistory(long Generation, long Revision, IReadOnlyList<AgentPaneRecord> Messages, IReadOnlyList<AgentBlockRecord> Blocks);
