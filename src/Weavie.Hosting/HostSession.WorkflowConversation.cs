using Weavie.Core.Agents;
using Weavie.Core.Workflows;
using Weavie.Workflows;

namespace Weavie.Hosting;

public sealed partial class HostSession {

	// One conversation a run record keeps for a fork or fresh stage, lent to one visit at a time; see
	// docs/specs/workflow-context-reuse and docs/specs/workflow-nested-stages.
	private sealed class WorkflowConversationOwner {
		private readonly HostSession _session;
		private readonly WorkflowStageConversation _description;
		private readonly StageLease _lease = new();
		private readonly SemaphoreSlim _delivery = new(1, 1);
		private readonly CancellationTokenSource _lifetime = new();
		private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private Weavie.Core.Mcp.CapabilityRegistryGrant? _tools;
		private IAgentConversation? _conversation;
		private bool _promptImagesSent;

		public AgentConversationKey Key { get; }

		public WorkflowConversationOwner(HostSession session, WorkflowStageConversation description) {
			_session = session;
			_description = description;
			Key = new(description.Key.Frame, description.Key.Opener);
		}

		private WorkflowStage Opener => _description.Opener;

		/// <summary>The record ended; <paramref name="completed"/> when the run went on, so the last completion reply is delivered first.</summary>
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
					_session.Notify("error", $"Couldn't close workflow stage {_description.Title}: {ex.Message}");
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
						if (kickoff.Continues && Live is { } live) {
							await live.ApplyControlsAsync(MainMode(live)).WaitAsync(stopping.Token).ConfigureAwait(false);
						}
						_lease.Visit = visit;
						_ = _session.Background.Run(token => ReturnAsync(visit, token));
					}
					bool opened = false;
					if (!kickoff.Continues || Live is null) {
						if (await EnsureAsync(visit, kickoff, stopping.Token).ConfigureAwait(false) is not { } opening) return;
						opened = opening;
					}
					if (kickoff.Opens.Length > 0) {
						// A stage's turn never steers the end of the previous stage's turn in a shared conversation.
						await _conversation!.WhenIdleAsync(stopping.Token).ConfigureAwait(false);
						_lease.Continues = kickoff.Continues && !opened;
					}
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

		/// <summary>
		/// The live conversation for a fork of it to copy, restored or opened when needed, under this owner's gate;
		/// null when it was lost and the run paused for it.
		/// </summary>
		public async Task<IAgentConversation?> SourceAsync(WorkflowStageExecution visit, WorkflowKickoff kickoff, CancellationToken ct) {
			using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
			await _delivery.WaitAsync(stopping.Token).ConfigureAwait(false);
			try {
				return Live is not null || await EnsureAsync(visit, kickoff, stopping.Token).ConfigureAwait(false) is not null ? _conversation : null;
			} finally {
				_delivery.Release();
			}
		}

		private IAgentConversation? Live => _conversation is { Completion.IsCompleted: false } live ? live : null;

		// A turn that continues restores the kept conversation; otherwise, or when there is nothing to restore, a new one
		// opens. A stage whose stages already used the conversation pauses the run instead, until the user resumes.
		// Whether a new conversation opened, or null when none is ready.
		private async Task<bool?> EnsureAsync(WorkflowStageExecution visit, WorkflowKickoff kickoff, CancellationToken ct) {
			if (_session.Agent.Session is not IStructuredAgentConversations conversations) {
				throw new WorkflowException("This agent cannot open a separate workflow conversation.");
			}
			await CloseAsync().ConfigureAwait(false);
			bool continues = _session.Workflow.HasOpened(_description.Key);
			string? lost = null;
			if (continues) {
				try {
					_conversation = await conversations.RestoreAsync(Binding(), ct).ConfigureAwait(false);
				} catch (Exception ex) when (ex is not OperationCanceledException) {
					lost = $"its earlier one could not be restored: {ex.Message}";
				}
			}
			bool opened = _conversation is null;
			if (opened) {
				bool renewing = kickoff.Renews == _description.Key;
				if (continues && Opener.Stages.Count > 0 && !renewing) {
					_session.Workflow.ConversationLost(kickoff, _description.Key, lost ?? "it ended");
					return null;
				}
				if (!await OpenAsync(conversations, visit, kickoff, ct).ConfigureAwait(false)) return null;
				_promptImagesSent = false;
				lost ??= renewing || !kickoff.Continues && visit.Stage.Reuse != Opener.Id ? null
					: continues ? "its earlier one ended"
					: $"stage \"{Opener.Id}\" has no conversation to continue";
				string who = visit.Stage.Id == Opener.Id ? _description.Title : visit.Stage.Id;
				if (lost is not null) _session.Agent.Structured?.Notice($"Workflow: {who} opened a new conversation; {lost}");
			}
			var current = _conversation!;
			try {
				await ApplyAgentAsync(current, ct).ConfigureAwait(false);
			} catch when (!ct.IsCancellationRequested) {
				// Unloading cancels instead, and leaves the conversation kept for the next load.
				await CloseAsync().ConfigureAwait(false);
				throw;
			}
			_ = _session.Background.Run(token => ObserveAsync(current, token));
			return opened;
		}

		// A fork copies the conversation it sits in, as that conversation stands once its turn has ended.
		private async Task<bool> OpenAsync(IStructuredAgentConversations conversations, WorkflowStageExecution visit, WorkflowKickoff kickoff, CancellationToken ct) {
			if (Opener.Context != WorkflowContext.Fork) {
				_conversation = await conversations.OpenFreshAsync(Binding(), ct).ConfigureAwait(false);
			} else if (_description.Source is not { } source) {
				_conversation = await conversations.OpenForkAsync(Binding(), ct).ConfigureAwait(false);
			} else if (await _session.ConversationOwner(source).SourceAsync(visit, kickoff, ct).ConfigureAwait(false) is { } parent) {
				await parent.WhenIdleAsync(ct).ConfigureAwait(false);
				_conversation = await parent.ForkAsync(Binding(), ct).ConfigureAwait(false);
			}
			return _conversation is not null;
		}

		private AgentConversationBinding Binding() => new() {
			Title = _description.Title,
			TurnContext = _lease,
			ControlHold = AgentControlHold.None,
			Tools = _tools = _session._registry.CreateGrant(() => _lease.Visit?.Capture()),
			Owner = Key,
		};

		private async Task ApplyAgentAsync(IAgentConversation conversation, CancellationToken ct) {
			if (_description.Agent is not { } agent) return;
			if (WorkflowAgentControls.Model(Opener.Id, agent, conversation.ControlState.Axes) is { } model) {
				await conversation.ApplyControlsAsync([model]).WaitAsync(ct).ConfigureAwait(false);
			}
			WorkflowAgentControls.CheckOptions(Opener.Id, agent, conversation.ControlState.Axes);
			await conversation.ApplyControlsAsync([.. agent.Options.Select(option => new AgentControlValue(option.Key, option.Value))]).WaitAsync(ct).ConfigureAwait(false);
		}
		// A workflow never sets a permission mode, so each visit runs in main's.
		private List<AgentControlValue> MainMode(IAgentConversation conversation) {
			var own = conversation.ControlState.Axes;
			return [.. (_session.Agent.Controls?.ControlState.Axes ?? [])
				.Where(axis => !WorkflowAgentControls.Changeable(axis) && own.Any(o => o.Id == axis.Id && o.Value != axis.Value))
				.Select(axis => new AgentControlValue(axis.Id, axis.Value))];
		}

		// The conversation is let go before it ends, so its observer never mistakes the close for a loss during a visit.
		private async Task CloseAsync() {
			var (tools, conversation) = (_tools, _conversation);
			(_tools, _conversation) = (null, null);
			if (tools is not null) await tools.DisposeAsync().ConfigureAwait(false);
			if (conversation is not null) await conversation.DisposeAsync().ConfigureAwait(false);
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
