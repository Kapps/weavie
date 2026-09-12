using Weavie.Core.Agents;
using Weavie.Core.Commands;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private Task<CommandResult> RecreateSessionAsync(
		HostSession? source,
		string? sessionId,
		string? agentProviderId,
		CommandInvocationContext context,
		CancellationToken ct) => RunSessionLifecycleAsync(async () => {
			if (string.IsNullOrWhiteSpace(sessionId) || _sessions?.Find(sessionId) is not { } target) {
				return CommandResult.Failure("Recreate needs an existing session id.");
			}
			if (string.IsNullOrWhiteSpace(agentProviderId)) {
				return CommandResult.Failure("Recreate needs an agent provider id.");
			}

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
					var result = await RunSessionLifecycleAsync(async () => {
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
			// Once teardown starts, finish the replacement even if its originating endpoint disconnects.
			await _ui.InvokeAsync(() => UnloadSlotAsync(target), CancellationToken.None).ConfigureAwait(false);
			provider.ClearConversation(target.WorktreePath);
			return await _ui.InvokeAsync(() => {
				if (_worktrees?.Registry.FindByPath(target.WorktreePath) is { } record) {
					_worktrees.Registry.Add(record with { AgentProviderId = provider.Info.Id });
				}
				target.AgentProviderId = provider.Info.Id;
				PersistSessionState();
				LoadSlotInBackground(target);
				return Task.FromResult(CommandResult.Success(
					$"Recreated '{target.Label}' with {provider.Info.Name}.", SessionActivationJson(target)));
			}, CancellationToken.None).ConfigureAwait(false);
		} catch (Exception ex) {
			return CommandResult.Failure($"Couldn't recreate '{target.Label}': {ex.Message}");
		}
	}
}
