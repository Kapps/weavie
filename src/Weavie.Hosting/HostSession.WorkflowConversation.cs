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

		public async Task RunLifetimeAsync(CancellationToken ct) {
			await Task.Yield();
			try {
				bool completed = await _ended.Task.WaitAsync(ct).ConfigureAwait(false);
				await _lifetime.CancelAsync().ConfigureAwait(false);
				await _delivery.WaitAsync(CancellationToken.None).ConfigureAwait(false);
				try {
					if (_tools is { } tools) {
						await tools.DisposeAsync().ConfigureAwait(false);
						if (completed) await tools.CompletionResponses.WaitAsync(ct).ConfigureAwait(false);
					}
					if (_conversation is { } conversation) await conversation.DisposeAsync().ConfigureAwait(false);
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
					bool reopened = false;
					if (_lease.Visit != visit) {
						await _session.ReturnMainForChildAsync(visit, stopping.Token).ConfigureAwait(false);
						if (_opener.Run.Context == WorkflowContext.Fork && visit.Stage == _opener) {
							await CloseAsync().ConfigureAwait(false);
							reopened = true;
						} else if (Live is { } live) {
							await live.ApplyControlsAsync(MainMode(live)).WaitAsync(stopping.Token).ConfigureAwait(false);
						}
						_lease.Visit = visit;
						_ = _session.Background.Run(token => ReturnAsync(visit, token));
					}
					bool opened = Live is null && await EnsureAsync(visit, reopened, stopping.Token).ConfigureAwait(false);
					if (kickoff.Opens.Length > 0) _lease.Continues = !opened;
					if (!_promptImagesSent && owed.Refusal is { } refusal) throw new WorkflowException(refusal);
					if (_session.Workflow.TryClaimKickoff(kickoff)) {
						try {
							var submission = kickoff.Submission(_promptImagesSent ? [] : owed.Images);
							_conversation!.Submit(submission);
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

		// Restores the kept conversation or opens a new one; true when it opened one.
		private async Task<bool> EnsureAsync(WorkflowStageExecution visit, bool reopened, CancellationToken ct) {
			if (_session.Agent.Session is not IStructuredAgentConversations conversations) {
				throw new WorkflowException("This agent cannot open a separate workflow conversation.");
			}
			await CloseAsync().ConfigureAwait(false);
			string? lost = null;
			if (!reopened) {
				try {
					_conversation = await conversations.RestoreAsync(Binding(), ct).ConfigureAwait(false);
				} catch (Exception ex) when (ex is not OperationCanceledException) {
					lost = $"its earlier one could not be restored: {ex.Message}";
				}
			}
			bool opened = _conversation is null;
			if (opened) {
				_conversation = await (_opener.Run.Context == WorkflowContext.Fork
					? conversations.OpenForkAsync(Binding(), ct)
					: conversations.OpenFreshAsync(Binding(), ct)).ConfigureAwait(false);
				_promptImagesSent = false;
				lost ??= reopened ? null
					: _held ? "its earlier one ended"
					: visit.Stage != _opener ? $"stage \"{_opener.Id}\" has no conversation to continue"
					: null;
				if (lost is not null) _session.Agent.Structured?.Notice($"Workflow: {visit.Stage.Id} opened a new conversation; {lost}");
			}
			_held = true;
			await ApplyAgentAsync(_conversation!, ct).ConfigureAwait(false);
			var current = _conversation!;
			_ = _session.Background.Run(token => ObserveAsync(current, token));
			return opened;
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

		// The visit ends: a visit that didn't complete stops its turn; the conversation stays for the next one.
		private async Task ReturnAsync(WorkflowStageExecution visit, CancellationToken ct) {
			using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
			try {
				bool completed = await visit.Retired.WaitAsync(stopping.Token).ConfigureAwait(false);
				await _delivery.WaitAsync(stopping.Token).ConfigureAwait(false);
				try {
					if (_lease.Visit != visit) return;
					_lease.Visit = null;
					if (!completed) Live?.Interrupt();
				} finally {
					_delivery.Release();
				}
			} catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
			}
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
