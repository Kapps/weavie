using System.Globalization;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession : IStructuredAgentRewind {
	/// <inheritdoc/>
	public Task RewindLatestAsync() => RewindBeforeAsync(Primary.TurnId());

	/// <inheritdoc/>
	public async Task RewindBeforeAsync(string turnId) {
		ArgumentException.ThrowIfNullOrEmpty(turnId);
		AcpConversation primary;
		RewindPort rewind;
		lock (_turnTransitionGate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			primary = _primary;
			if (!primary.Ready || !Forkable) throw new InvalidOperationException($"{_definition.Name} cannot rewind this conversation now.");
			if (primary.Rewinding || primary.HasWork || Sides().Any(side => side.Conversation.HasWork)) {
				throw new InvalidOperationException("Wait for the agent to finish before rewinding.");
			}
			var plan = AcpRewindPlan.Create(_sessions.ReadMessages(_definition.Id, _context.Workspace), turnId);
			if (plan.ForkMessageId is not { } messageId) {
				CommitRewind(primary, plan, rewind: null);
				return;
			}
			primary.Rewinding = true;
			rewind = new RewindPort(this, primary, plan, messageId);
			Open(rewind.Fork, _process!);
		}
		try {
			await rewind.Adopted.ConfigureAwait(false);
		} catch (Exception error) {
			rewind.Discard();
			if (error is ObjectDisposedException && !primary.Live) throw new InvalidOperationException("The conversation was replaced during the rewind.", error);
			throw;
		} finally {
			primary.Rewinding = false;
			primary.DispatchPendingSubmission();
		}
	}

	// The fork, once its replay proved the rewind point, becomes the primary on this process; an agent that cannot
	// close the predecessor's session restarts onto the fork instead. A first-prompt rewind starts a fresh session.
	private void CommitRewind(AcpConversation predecessor, AcpRewindPlan plan, RewindPort? rewind) {
		if (!predecessor.Live) throw new InvalidOperationException("The conversation was replaced during the rewind.");
		var sides = Sides();
		var continuation = rewind?.Fork.Continuation ?? RewindContinuation(predecessor, plan);
		string[] dropped;
		try {
			predecessor.TerminalizeForRestart(clearSubmissions: false, "Conversation rewound.");
			SuspendSides("Conversation rewound.");
			predecessor.SettleInteractions();
			dropped = [.. _sideConversations.Values.Where(side => !plan.Keeps(side.AnchorTurnNumber)).Select(side => side.ConversationId)];
			_sessions.Replace(_definition.Id, _context.Workspace, [continuation, .. _sideConversations.Values.Where(side => !dropped.Contains(side.ConversationId))], plan.Kept);
		} catch (AcpSessionStoreException error) {
			StopForStorageFailure(error, sides);
			throw;
		}
		foreach (string conversationId in dropped) _sideConversations.Remove(conversationId);
		var process = ReplaceableProcess(predecessor);
		var adopted = process is null ? null : rewind;
		var successor = Succeed(predecessor, process, handoff => {
			if (adopted is null) {
				rewind?.Fork.Retire();
				return CreatePrimary(handoff with { Continuation = continuation });
			}
			adopted.Fork.Inherit(handoff);
			return adopted.Fork;
		});
		PaneSnapshot?.Invoke(plan.Kept);
		if (adopted is null) Launch(successor, process);
		else adopted.Promote();
		if (plan.Prompt.Length > 0) successor.Prefill(plan.Prompt);
	}

	private static AcpConversationState RewindContinuation(AcpConversation predecessor, AcpRewindPlan plan) {
		var state = predecessor.Continuation;
		return state with {
			SessionId = null,
			TurnNumber = plan.Turn - 1,
			GuidanceSent = plan.ForkMessageId is not null && state.GuidanceSent,
			PlanTurns = state.PlanTurns.Where(pair => plan.Keeps(long.Parse(pair.Value, CultureInfo.InvariantCulture)))
				.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
		};
	}

	// A rewind's fork publishes nothing until its replay proves the rewind point and the owner commits it.
	private sealed class RewindPort : PrimaryPort {
		private readonly AcpConversation _predecessor;
		private readonly AcpRewindPlan _plan;
		private readonly TaskCompletionSource _adopted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private bool _staged = true;

		public RewindPort(AcpAgentSession owner, AcpConversation predecessor, AcpRewindPlan plan, string messageId) : base(owner) {
			_predecessor = predecessor;
			_plan = plan;
			Fork = new AcpConversation(owner._host, new AcpConversationSpec(
				AcpConversationHandoff.Fresh(RewindContinuation(predecessor, plan)),
				new ForkFromOpening(predecessor, messageId),
				AcpConversationRole.Primary), this);
		}

		public AcpConversation Fork { get; }

		/// <summary>Completes once the fork is committed; faults when it failed or could not be committed.</summary>
		public Task Adopted => _adopted.Task;

		protected override bool Staged => _staged;

		/// <summary>Connects the committed fork to the owner.</summary>
		public void Promote() {
			_staged = false;
			Fork.Announce();
		}

		/// <summary>Retires a fork that was never committed, closing its provider session.</summary>
		public void Discard() {
			lock (Owner._turnTransitionGate) {
				if (!_staged) return;
				if (Owner.Features.Close) Fork.RetireClosing();
				else Fork.Retire();
			}
		}

		protected override void OnOpened() {
			try {
				Owner.CommitRewind(_predecessor, _plan, this);
			} catch (Exception error) {
				_adopted.TrySetException(error);
				throw;
			}
			_adopted.TrySetResult();
		}

		protected override bool OnFail(Exception error) {
			if (!_staged) return base.OnFail(error);
			_adopted.TrySetException(error);
			return false;
		}
	}
}
