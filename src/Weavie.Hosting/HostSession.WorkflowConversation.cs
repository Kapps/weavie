using Weavie.Core.Agents;
using Weavie.Core.Workflows;
using Weavie.Workflows;

namespace Weavie.Hosting;

public sealed partial class HostSession {

	// One conversation a run keeps for a fork or fresh stage, lent to one visit at a time; see docs/specs/workflow-context-reuse.
	private sealed class WorkflowConversationOwner {
		private readonly HostSession _session;
		private readonly WorkflowStage _opener;
		private readonly WorkflowAgent? _agent;
		private readonly StageLease _lease = new();
		private readonly SemaphoreSlim _delivery = new(1, 1);
		private readonly CancellationTokenSource _lifetime = new();
		private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private Weavie.Core.Mcp.CapabilityRegistryGrant? _tools;
		private IAgentConversation? _conversation;
		private bool _held;
		private bool _refork;
		private bool _turned;
		private bool _promptImagesSent;

		public AgentConversationKey Key { get; }

		public WorkflowConversationOwner(HostSession session, string runId, WorkflowStage opener, WorkflowAgent? agent) {
			_session = session;
			_opener = opener;
			_agent = agent;
			Key = new(runId, opener.Id);
		}

		/// <summary>The run ended; <paramref name="completed"/> when it finished, so the last completion reply is delivered first.</summary>
		public void End(bool completed) => _ended.TrySetResult(completed);

		/// <summary>Whether <paramref name="visit"/> borrows the conversation.</summary>
		public bool Lends(WorkflowStageExecution visit) => _lease.Visit == visit;

		public async Task RunLifetimeAsync(CancellationToken ct) {
			await Task.Yield();
			try {
				bool completed = await _ended.Task.WaitAsync(ct).ConfigureAwait(false);
				await _lifetime.CancelAsync().ConfigureAwait(false);
				await _delivery.WaitAsync(CancellationToken.None).ConfigureAwait(false);
				try {
					if (completed && _tools is { } tools) {
						await tools.DisposeAsync().ConfigureAwait(false);
						await tools.CompletionResponses.WaitAsync(ct).ConfigureAwait(false);
					}
					await CloseAsync().ConfigureAwait(false);
				} catch (Exception ex) when (ex is not OperationCanceledException) {
					_session.Notify("error", $"Couldn't close workflow stage {_opener.Id}: {ex.Message}");
				} finally {
					_delivery.Release();
				}
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				// The session is unloading: its agent ends the conversation and keeps it for the next load.
				await _lifetime.CancelAsync().ConfigureAwait(false);
			}
		}

		public async Task SendAsync(WorkflowStageExecution visit, OwedWorkflowTurn owed, CancellationToken ct) {
			var kickoff = owed.Kickoff;
			// Runner callbacks only enqueue. Opening and closure must never run under its state lock.
			await Task.Yield();
			using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
			try {
				await _delivery.WaitAsync(stopping.Token).ConfigureAwait(false);
				try {
					if (!_session.Workflow.Owes(kickoff)) return;
					if (_lease.Visit != visit) {
						if (_lease.Visit is { } previous) Release(previous);
						await _session.ReturnMainForChildAsync(visit, stopping.Token).ConfigureAwait(false);
						if (_opener.Run.Context == WorkflowContext.Fork && visit.Stage == _opener) {
							await CloseAsync().ConfigureAwait(false);
							_refork = true;
						} else if (Live is { } live) {
							await live.ApplyControlsAsync(MainMode(live)).WaitAsync(stopping.Token).ConfigureAwait(false);
						}
						_lease.Visit = visit;
						_ = _session.Background.Run(token => ReturnAsync(visit, token));
					}
					if (Live is null) await EnsureAsync(visit, stopping.Token).ConfigureAwait(false);
					if (kickoff.Opens.Length > 0) _lease.Continues = _turned;
					if (!_promptImagesSent && owed.Refusal is { } refusal) throw new WorkflowException(refusal);
					if (_session.Workflow.TryClaimKickoff(kickoff)) {
						try {
							var submission = kickoff.Submission(_promptImagesSent ? [] : owed.Images);
							_conversation!.Submit(submission);
							_turned = true;
							_promptImagesSent |= submission.Attachments.Count > 0;
						} catch (Exception ex) { visit.ConversationEnded(ex.Message); }
					}
				} finally {
					_delivery.Release();
				}
			} catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
			} catch (Exception ex) {
				_session.Workflow.RefuseOpen(kickoff, ex.Message);
			}
		}

		private IAgentConversation? Live => _conversation is { Completion.IsCompleted: false } live ? live : null;

		// Restores the kept conversation or opens a new one on the stage's agent; a conversation that can't take it closes.
		private async Task EnsureAsync(WorkflowStageExecution visit, CancellationToken ct) {
			if (_session.Agent.Session is not IStructuredAgentConversations conversations) {
				throw new WorkflowException("This agent cannot open a separate workflow conversation.");
			}
			await CloseAsync().ConfigureAwait(false);
			string? lost = null;
			if (!_refork) {
				try {
					_conversation = await conversations.RestoreAsync(Binding(), ct).ConfigureAwait(false);
					_turned = _conversation is not null;
				} catch (Exception ex) when (ex is not OperationCanceledException) {
					lost = $"its earlier one could not be restored: {ex.Message}";
				}
			}
			if (_conversation is null) {
				_conversation = await (_opener.Run.Context == WorkflowContext.Fork
					? conversations.OpenForkAsync(Binding(), ct)
					: conversations.OpenFreshAsync(Binding(), ct)).ConfigureAwait(false);
				_turned = _promptImagesSent = false;
				lost ??= _refork ? null
					: _held ? "its earlier one ended"
					: visit.Stage != _opener ? $"stage \"{_opener.Id}\" has no conversation to continue"
					: null;
				if (lost is not null) _session.Agent.Structured?.Notice($"Workflow: {visit.Stage.Id} opened a new conversation; {lost}");
			}
			try {
				await ApplyAgentAsync(_conversation, ct).ConfigureAwait(false);
			} catch when (!ct.IsCancellationRequested) {
				// Unloading cancels instead, and leaves the conversation kept for the next load.
				await CloseAsync().ConfigureAwait(false);
				throw;
			}
			_held = true;
			_refork = false;
			var current = _conversation;
			_ = _session.Background.Run(token => ObserveAsync(current, token));
		}

		private AgentConversationBinding Binding() => new() {
			Title = _opener.Id,
			TurnContext = _lease,
			ControlHold = AgentControlHold.None,
			Tools = _tools = _session._registry.CreateGrant(() => _lease.Visit?.Capture()),
			Owner = Key,
		};

		private async Task ApplyAgentAsync(IAgentConversation conversation, CancellationToken ct) {
			if (_agent is null) return;
			if (WorkflowAgentControls.Model(_opener.Id, _agent, conversation.ControlState.Axes) is { } model) {
				await conversation.ApplyControlsAsync([model]).WaitAsync(ct).ConfigureAwait(false);
			}
			WorkflowAgentControls.CheckOptions(_opener.Id, _agent, conversation.ControlState.Axes);
			await conversation.ApplyControlsAsync([.. _agent.Options.Select(option => new AgentControlValue(option.Key, option.Value))]).WaitAsync(ct).ConfigureAwait(false);
		}

		// A workflow never sets a permission mode, so each visit runs in main's.
		private List<AgentControlValue> MainMode(IAgentConversation conversation) {
			var own = conversation.ControlState.Axes;
			return [.. (_session.Agent.Controls?.ControlState.Axes ?? [])
				.Where(axis => !WorkflowAgentControls.Changeable(axis) && own.Any(o => o.Id == axis.Id && o.Value != axis.Value))
				.Select(axis => new AgentControlValue(axis.Id, axis.Value))];
		}

		private async Task CloseAsync() {
			if (_tools is { } tools) await tools.DisposeAsync().ConfigureAwait(false);
			if (_conversation is { } conversation) await conversation.DisposeAsync().ConfigureAwait(false);
			_tools = null;
			_conversation = null;
		}

		// The visit ends, whether this or the next visit's hand-over sees it first; the conversation stays for the next one.
		private async Task ReturnAsync(WorkflowStageExecution visit, CancellationToken ct) {
			using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
			try {
				await visit.Retired.WaitAsync(stopping.Token).ConfigureAwait(false);
				await _delivery.WaitAsync(stopping.Token).ConfigureAwait(false);
				try {
					Release(visit);
				} finally {
					_delivery.Release();
				}
			} catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
			}
		}

		// A visit left by a jump or cancel stops its turn, so nothing it still does reaches the next visit.
		private void Release(WorkflowStageExecution visit) {
			if (_lease.Visit != visit) return;
			_lease.Visit = null;
			if (visit.Retired is not { IsCompleted: true, Result: true }) Live?.Interrupt();
		}

		private async Task ObserveAsync(IAgentConversation conversation, CancellationToken ct) {
			using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
			try {
				var outcome = await conversation.Completion.WaitAsync(stopping.Token).ConfigureAwait(false);
				if (!ReferenceEquals(_conversation, conversation) || _lease.Visit is not { } visit || visit.Retired.IsCompleted) return;
				visit.ConversationEnded($"Stage conversation ended: {outcome.Error?.Message ?? outcome.Reason}. Resume to continue.");
			} catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
			}
		}
	}

	// Which visit, if any, the stage conversation's turns and tool calls belong to.
	private sealed class StageLease : IAgentTurnContext {
		public volatile WorkflowStageExecution? Visit;
		public volatile bool Continues;
		public string Compose() => Visit?.Compose(Continues) ?? "";
	}

}
