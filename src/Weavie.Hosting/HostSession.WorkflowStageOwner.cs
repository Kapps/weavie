using Weavie.Core.Agents;
using Weavie.Core.Workflows;
using Weavie.Workflows;

namespace Weavie.Hosting;

public sealed partial class HostSession {

	private sealed class WorkflowStageOwner {
		private readonly HostSession _session;
		private readonly WorkflowAgent? _agent;
		private Weavie.Core.Mcp.CapabilityRegistryGrant? _tools;
		private readonly SemaphoreSlim _delivery = new(1, 1);
		private readonly CancellationTokenSource _lifetime = new();
		private readonly CancellationToken _stopping;
		private IAgentConversation? _conversation;
		private bool _promptImagesSent;

		public WorkflowStageExecution Execution { get; }

		public WorkflowStageOwner(HostSession session, WorkflowStageExecution execution, WorkflowAgent? agent) {
			_session = session;
			_agent = agent;
			Execution = execution;
			_stopping = _lifetime.Token;
		}

		public async Task RunLifetimeAsync(CancellationToken ct) {
			await Task.Yield();
			try {
				bool completed = await Execution.Retired.WaitAsync(ct).ConfigureAwait(false);
				if (_tools is { } tools) {
					await tools.DisposeAsync().ConfigureAwait(false);
					if (completed) await tools.CompletionResponses.WaitAsync(ct).ConfigureAwait(false);
				}
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			} finally {
				await _lifetime.CancelAsync().ConfigureAwait(false);
				await _delivery.WaitAsync(CancellationToken.None).ConfigureAwait(false);
				try {
					if (_conversation is { } conversation) await conversation.DisposeAsync().ConfigureAwait(false);
				} catch (Exception ex) {
					_session.Notify("error", $"Couldn't close workflow stage {Execution.Stage.Id}: {ex.Message}");
				} finally {
					_delivery.Release();
					_lifetime.Dispose();
				}
			}
		}

		public async Task SendAsync(OwedWorkflowTurn owed, CancellationToken ct) {
			var kickoff = owed.Kickoff;
			// Runner callbacks only enqueue. Opening and closure must never run under its state lock.
			await Task.Yield();
			using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping);
			try {
				await _delivery.WaitAsync(stopping.Token).ConfigureAwait(false);
				try {
					if (!_session.Workflow.Owes(kickoff)) return;
					if (_conversation is null || _conversation.Completion.IsCompleted) {
						if (_conversation is not null) await _conversation.DisposeAsync().ConfigureAwait(false);
						_conversation = await OpenAsync(stopping.Token).ConfigureAwait(false);
						_promptImagesSent = false;
						var opened = _conversation;
						_ = _session.Background.Run(token => ObserveAsync(opened, token));
					}
					if (!_promptImagesSent && owed.Refusal is { } refusal) throw new WorkflowException(refusal);
					if (_session.Workflow.TryClaimKickoff(kickoff)) {
						try {
							var submission = kickoff.Submission(_promptImagesSent ? [] : owed.Images);
							_conversation.Submit(submission);
							_promptImagesSent |= submission.Attachments.Count > 0;
						} catch (Exception ex) { Execution.ConversationEnded(ex.Message); }
					}
				} finally {
					_delivery.Release();
				}
			} catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
			} catch (Exception ex) {
				_session.Workflow.RefuseOpen(kickoff, ex.Message);
			}
		}

		private async Task<IAgentConversation> OpenAsync(CancellationToken ct) {
			if (_session.Agent.Session is not IStructuredAgentConversations conversations) {
				throw new WorkflowException("This agent cannot open a separate workflow conversation.");
			}
			await _session.ReturnMainForChildAsync(Execution, ct).ConfigureAwait(false);
			var binding = new AgentConversationBinding {
				TurnContext = new StageTurnContext(Execution),
				ControlHold = AgentControlHold.None,
				Tools = _tools = _session._registry.CreateGrant(Execution.Capture, Execution.Stage.Id),
			};
			IAgentConversation? conversation = null;
			try {
				conversation = Execution.Stage.Run.Context == WorkflowContext.Fork
					? await conversations.OpenForkAsync(binding, ct).ConfigureAwait(false)
					: await conversations.OpenFreshAsync(binding, ct).ConfigureAwait(false);
				if (_agent is not null) {
					if (WorkflowAgentControls.Model(Execution.Stage.Id, _agent, conversation.ControlState.Axes) is { } model) {
						await conversation.ApplyControlsAsync([model]).WaitAsync(ct).ConfigureAwait(false);
					}
					WorkflowAgentControls.CheckOptions(Execution.Stage.Id, _agent, conversation.ControlState.Axes);
					await conversation.ApplyControlsAsync([.. _agent.Options.Select(option => new AgentControlValue(option.Key, option.Value))]).WaitAsync(ct).ConfigureAwait(false);
				}
				ct.ThrowIfCancellationRequested();
				return conversation;
			} catch {
				if (conversation is not null) await conversation.DisposeAsync().ConfigureAwait(false);
				await binding.Tools.DisposeAsync().ConfigureAwait(false);
				throw;
			}
		}

		private async Task ObserveAsync(IAgentConversation conversation, CancellationToken ct) {
			using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping);
			try {
				var outcome = await conversation.Completion.WaitAsync(stopping.Token).ConfigureAwait(false);
				if (Execution.Retired.IsCompleted || !ReferenceEquals(_conversation, conversation)) return;
				Execution.ConversationEnded($"Stage conversation ended: {outcome.Error?.Message ?? outcome.Reason}. Resume to open a new conversation.");
			} catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
			}
		}
	}

	private sealed class StageTurnContext(WorkflowStageExecution execution) : IAgentTurnContext {
		public string Compose() => execution.Compose();
	}

}
