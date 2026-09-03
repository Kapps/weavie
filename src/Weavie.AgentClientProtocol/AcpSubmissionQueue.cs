using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

/// <summary>
/// The accepted submissions one ACP session has not delivered yet, in delivery order. Every mutation bumps
/// <see cref="Version"/>, so the session publishes the waiting set without any call site remembering to.
/// </summary>
internal sealed class AcpSubmissionQueue {
	private readonly LinkedList<AgentTurnSubmission> _items = [];

	/// <summary>Increments on every mutation.</summary>
	public long Version { get; private set; }

	/// <summary>The number of submissions still waiting.</summary>
	public int Count => _items.Count;

	/// <summary>Adds a submission behind everything already waiting.</summary>
	public void Enqueue(AgentTurnSubmission submission) {
		_items.AddLast(submission);
		Version++;
	}

	/// <summary>Returns a submission that could not be delivered to the front of the queue.</summary>
	public void Requeue(AgentTurnSubmission submission) {
		_items.AddFirst(submission);
		Version++;
	}

	/// <summary>Removes and returns the first submission <paramref name="deliverable"/> accepts, or null.</summary>
	public AgentTurnSubmission? Take(Func<AgentTurnSubmission, bool> deliverable) {
		ArgumentNullException.ThrowIfNull(deliverable);
		for (var node = _items.First; node is not null; node = node.Next) {
			if (!deliverable(node.Value)) continue;
			_items.Remove(node);
			Version++;
			return node.Value;
		}
		return null;
	}

	/// <summary>Drops every waiting submission.</summary>
	public void Clear() {
		if (_items.Count == 0) return;
		_items.Clear();
		Version++;
	}

	/// <summary>The waiting submissions, in delivery order.</summary>
	public AgentTurnSubmission[] Snapshot() => [.. _items];
}
