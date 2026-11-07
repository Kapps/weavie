using Weavie.Core.Agents;
using Weavie.Core.Commands;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private Task<CommandResult> RecreateSessionAsync(
		HostSession? source,
		string? sessionId,
		string? agentProviderId,
		bool stopBackgroundWork,
		CommandInvocationContext context,
		CancellationToken ct) => RunSlotLifecycleAsync(sessionId, "Recreate needs an existing session id.", async target => {
			if (string.IsNullOrWhiteSpace(agentProviderId)) {
				return CommandResult.Failure("Recreate needs an agent provider id.");
			}
			if (BackgroundWorkRefusal(target.Session, stopBackgroundWork) is { } refusal) return refusal;

			IAgentProvider provider;
			try {
				provider = _agentProviders.RequireAvailable(agentProviderId);
			} catch (InvalidOperationException ex) {
				return CommandResult.Failure(ex.Message);
			}
			if (target.Session is { } session
				&& await FlushSessionViewAsync(session, ct).ConfigureAwait(false) is { } failure) {
				return failure;
			}
			if (source is not null && ReferenceEquals(target.Session, source)) {
				context.AfterReply(async cancellation => {
					var result = await GatedAsync(target.Lifecycle, async () => {
						if (!ReferenceEquals(_sessions?.Find(target.Id), target) || !ReferenceEquals(target.Session, source)) {
							return CommandResult.Failure("The session changed before it could be recreated.");
						}
						return await RecreateSlotAsync(target, provider, cancellation).ConfigureAwait(false);
					}, cancellation).ConfigureAwait(false);
					Notify(result.Ok ? "info" : "error", result.Ok
						? $"Recreated '{target.Label}' with {provider.Info.Name}."
						: result.Error!);
				});
				return CommandResult.Success("The session will be recreated after this reply.");
			}
			return await RecreateSlotAsync(target, provider, ct).ConfigureAwait(false);
		}, ct);

	private async Task<CommandResult> RecreateSlotAsync(SessionSlot target, IAgentProvider provider, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		try {
			await ReplaceSlotAsync(target, () => {
				provider.ClearConversation(target.WorktreePath);
				if (_worktrees?.Registry.FindByPath(target.WorktreePath) is { } record) {
					_worktrees.Registry.Add(record with { AgentProviderId = provider.Info.Id });
				}
				target.AgentProviderId = provider.Info.Id;
			}).ConfigureAwait(false);
			return CommandResult.Success($"Recreated '{target.Label}' with {provider.Info.Name}.", SessionActivationJson(target));
		} catch (Exception ex) {
			return CommandResult.Failure($"Couldn't recreate '{target.Label}': {ex.Message}");
		}
	}

	// One catalog push swaps the incarnation, so selection never sees the slot go dormant. Once teardown starts it
	// finishes even if the originating endpoint disconnects.
	private async Task ReplaceSlotAsync(SessionSlot target, Action prepare) {
		var session = target.Session;
		try {
			if (session is not null) {
				await session.DisposeAsync().ConfigureAwait(false);
			}
		} finally {
			await _ui.InvokeAsync(() => {
				if (session is not null && ReferenceEquals(target.Session, session)) {
					target.Session = null;
					_fileRoutes.Unregister(session.Incarnation);
				}

				try {
					prepare();
					LoadSlotInBackground(target);
				} finally {
					PushSessionList();
					PersistSessionState();
				}

				return Task.CompletedTask;
			}, CancellationToken.None).ConfigureAwait(false);
		}
	}
}
