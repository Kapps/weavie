using Weavie.Core.Commands;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// Lifecycle gates: one per slot so a slow delete or unload never stalls another session, plus the catalog gate
// for operations that allocate a new slot, branch, or worktree. Order is always catalog, then slot.
public sealed partial class HostCore {
	/// <summary>Runs <paramref name="action"/> under the slot <paramref name="sessionId"/> names, while it is still cataloged.</summary>
	private Task<CommandResult> RunSlotLifecycleAsync(
		string? sessionId,
		string missing,
		Func<SessionSlot, Task<CommandResult>> action,
		CancellationToken ct) {
		if (string.IsNullOrWhiteSpace(sessionId) || _sessions?.Find(sessionId) is not { } slot) {
			return Task.FromResult(CommandResult.Failure(missing));
		}

		return GatedAsync(slot.Lifecycle, () => ReferenceEquals(_sessions?.Find(slot.Id), slot)
			? action(slot)
			: Task.FromResult(CommandResult.Failure(missing)), ct);
	}

	/// <summary>Waits out lifecycle work already admitted on the source, then returns it if still that exact live session.</summary>
	private async Task<SessionSlot?> CurrentSourceAsync(SessionAddress source, CancellationToken ct) {
		if (_sessions?.Find(source.Slot) is not { } slot) {
			return null;
		}

		return await GatedAsync(slot.Lifecycle, () => Task.FromResult(
			ReferenceEquals(_sessions?.Find(slot.Id), slot) && slot.Session?.Address == source ? slot : null), ct)
			.ConfigureAwait(false);
	}

	private static CommandResult SourceGone() => CommandResult.Failure("The source session no longer exists.");

	private static async Task<T> GatedAsync<T>(SemaphoreSlim gate, Func<Task<T>> action, CancellationToken ct) {
		await gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			return await action().ConfigureAwait(false);
		} finally {
			gate.Release();
		}
	}

	private static async Task GatedAsync(SemaphoreSlim gate, Func<Task> action, CancellationToken ct) {
		await gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			await action().ConfigureAwait(false);
		} finally {
			gate.Release();
		}
	}
}
