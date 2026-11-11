namespace Weavie.Core.Agents;

/// <summary>Enqueues ordered output without calling conversation controllers or UI observers.</summary>
public interface IConversationOutputSink {
	/// <summary>Enqueues one committed revision.</summary>
	void Live(AgentPaneRecord record);
	/// <summary>Enqueues replacement of the visible history.</summary>
	void Reset();
}

/// <summary>The session's single output ledger, durable-write boundary, and ordered publication source.</summary>
public sealed class ConversationOutput(IConversationOutputSink sink) {
	private readonly Lock _gate = new();
	private readonly AgentPaneLedger _ledger = new();
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
				sink.Live(Record(_ledger.Store(message)));
			}
		}
	}

	/// <summary>Loads saved history before the session is addressable.</summary>
	public void Restore(IReadOnlyList<AgentPaneMessage> messages) => Replace(messages, publish: false);

	/// <summary>Replaces history and publishes its new generation and records.</summary>
	public void Replace(IReadOnlyList<AgentPaneMessage> messages) => Replace(messages, publish: true);

	private void Replace(IReadOnlyList<AgentPaneMessage> messages, bool publish) {
		lock (_gate) {
			if (_ledger.Count > 0) {
				Reset();
				if (publish) sink.Reset();
			}
			foreach (var message in messages) {
				var record = Record(_ledger.Store(message));
				if (publish) sink.Live(record);
			}
		}
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
				.Select(index => Record(_ledger.EntryAt(index)))]);
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

	private void Reset() {
		_generation++;
		_ledger.Clear();
	}
}

/// <summary>A passive view of a conversation's rows; the controller owns its execution.</summary>
public sealed class ConversationView(ConversationOutput output, string providerId, string? conversationId) {
	/// <summary>The announced parent scope, when this is a provider-created child.</summary>
	public string? ParentConversationId => output.ParentConversation(providerId, conversationId);

	/// <summary>The current materialized transcript.</summary>
	public IReadOnlyList<AgentPaneMessage> Rows => output.ReadView(providerId, conversationId, active: false);

	/// <summary>Materializes unfinished agent content for the controller to complete and persist.</summary>
	public IReadOnlyList<AgentPaneMessage> CompleteContent() => [.. output.ReadView(providerId, conversationId, active: true)
		.Where(row => row.ItemType is "agentMessage" or "thought")
		.Select(row => row with {
			Type = "item-completed", Status = "completed", Text = row.Text is { Length: > 0 } ? row.Text : null,
			Category = row.ItemType == "thought" ? "thought" : null,
			Summary = row.ItemType == "thought" ? "Reasoning" : null,
		})];
}

/// <summary>A materialized row or live update at an exact generation, ordinal, and revision.</summary>
public sealed record AgentPaneRecord(long Generation, long Ordinal, long Revision, AgentPaneMessage Message);

/// <summary>A consistent full or incremental snapshot of a session's output.</summary>
public sealed record AgentPaneHistory(long Generation, long Revision, IReadOnlyList<AgentPaneRecord> Messages);
