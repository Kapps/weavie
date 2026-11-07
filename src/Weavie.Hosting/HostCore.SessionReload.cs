using Weavie.Core.Commands;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	// No editor flush: it can block on the very save that made this reload necessary. Each page carries its own edits.
	private Task<CommandResult> ReloadSessionAsync(
		HostSession? source,
		string? sessionId,
		bool stopBackgroundWork,
		CommandInvocationContext context,
		CancellationToken ct) => RunSlotLifecycleAsync(sessionId, "Reload needs an existing session id.", async target => {
			if (target.Session is null) {
				return CommandResult.Failure($"'{target.Label}' isn't loaded.");
			}
			if (BackgroundWorkRefusal(target.Session, stopBackgroundWork) is { } refusal) return refusal;
			if (source is not null && ReferenceEquals(target.Session, source)) {
				context.AfterReply(async cancellation => {
					var result = await GatedAsync(target.Lifecycle, async () => ReferenceEquals(target.Session, source)
						? await ReloadSlotAsync(target).ConfigureAwait(false)
						: CommandResult.Failure("The session changed before it could be reloaded."), cancellation).ConfigureAwait(false);
					if (!result.Ok) {
						Notify("error", result.Error!);
					}
				});
				return CommandResult.Success("The session will be reloaded after this reply.");
			}
			return await ReloadSlotAsync(target).ConfigureAwait(false);
		}, ct);

	private async Task<CommandResult> ReloadSlotAsync(SessionSlot target) {
		try {
			await ReplaceSlotAsync(target, static () => { }).ConfigureAwait(false);
			return CommandResult.Success($"Reloaded '{target.Label}'.");
		} catch (Exception ex) {
			return CommandResult.Failure($"Couldn't reload '{target.Label}': {ex.Message}");
		}
	}

	private Task<CommandResult> CancelOperationAsync(string? operationId) => Task.FromResult(
		operationId is not null && _messages.TryCancelOperation(operationId)
			? CommandResult.Success("Cancelling.")
			: CommandResult.Failure("That operation already finished."));
}
