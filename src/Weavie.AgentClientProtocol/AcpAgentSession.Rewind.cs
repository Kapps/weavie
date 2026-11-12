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
		RewindPort? rewind = null;
		lock (_turnTransitionGate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			primary = _primary;
			if (!primary.Ready || !Forkable) throw new InvalidOperationException($"{_definition.Name} cannot rewind this conversation now.");
			if (primary.Rewinding || Roots().Any(conversation => conversation.Independent ? !conversation.Settled : conversation.HasWork)) {
				throw new InvalidOperationException("Wait for the agent to finish before rewinding.");
			}
			var plan = AcpRewindPlan.Create(_sessions.ReadMessages(_definition.Id, _context.Workspace), turnId);
			primary.Rewinding = true;
			try {
				foreach (var child in _runtime.Conversations.Where(conversation => conversation.Independent)) _ = _runtime.CloseAsync(child);
				Forget(_ => true);
				if (plan.ForkMessageId is not { } messageId) {
					CommitRewind(primary, plan, rewind: null);
					primary.Rewinding = false;
					return;
				}
				rewind = new RewindPort(this, primary, plan, messageId);
				_runtime.Open(rewind.Fork, _runtime.Process!);
			} catch {
				primary.Rewinding = false;
				throw;
			}
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
		// Decided before settling: settling ends the background work only a restart actually stops.
		var process = ReplaceableProcess(predecessor);
		var continuation = rewind?.Fork.Continuation ?? RewindContinuation(predecessor, plan);
		string[] dropped;
		IReadOnlyList<AgentPaneMessage> kept;
		try {
			predecessor.TerminalizeForRestart(clearSubmissions: false, "Conversation rewound.");
			SuspendSides("Conversation rewound.");
			predecessor.SettleInteractions();
			// Nested conversations kept running while the fork opened, and settling them just journalled their ends.
			kept = AcpRewindPlan.Create(_sessions.ReadMessages(_definition.Id, _context.Workspace), plan.Turn.ToString(CultureInfo.InvariantCulture)).Kept;
			dropped = [.. _sideConversations.Values.Where(side => !plan.Keeps(side.AnchorTurnNumber)).Select(side => side.ConversationId)];
			_sessions.Replace(_definition.Id, _context.Workspace, [continuation, .. _sideConversations.Values.Where(side => !dropped.Contains(side.ConversationId))], kept);
		} catch (AcpSessionStoreException error) {
			StopForStorageFailure(error);
			throw;
		}
		foreach (string conversationId in dropped) _sideConversations.Remove(conversationId);
		var adopted = process is null ? null : rewind;
		var successor = Succeed(predecessor, handoff => {
			if (adopted is null) {
				rewind?.Fork.Retire();
				return CreatePrimary(handoff with { Continuation = continuation });
			}
			adopted.Fork.Inherit(handoff);
			return adopted.Fork;
		});
		Output.Replace(kept);
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
			Fork = owner._runtime.Create(new AcpConversationSpec(
				AcpConversationHandoff.Fresh(RewindContinuation(predecessor, plan)),
				new ForkFromOpening(predecessor, messageId),
				AcpConversationRole.Primary), this, owner._context.Bindings.Main());
		}

		public AcpConversation Fork { get; }

		/// <summary>Completes once the fork is committed; faults when it failed or could not be committed.</summary>
		public Task Adopted => WaitForAdoptionAsync();

		private async Task WaitForAdoptionAsync() {
			await Task.WhenAny(_adopted.Task, Fork.Completion).ConfigureAwait(false);
			lock (Owner._turnTransitionGate) {
				if (!_adopted.Task.IsCompleted) throw new ObjectDisposedException(nameof(AcpConversation));
			}
			await _adopted.Task.ConfigureAwait(false);
		}

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
				Fork.Retire();
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
