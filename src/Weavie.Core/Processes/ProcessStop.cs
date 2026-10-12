using System.Globalization;

namespace Weavie.Core.Processes;

/// <summary>Runs a <see cref="ProcessStopPlan"/>: ask, wait for the exit until the deadline, then force-kill.</summary>
public static class ProcessStop {
	private static readonly Task<string?> Unanswered = new TaskCompletionSource<string?>().Task;

	/// <summary>
	/// Asks the child to exit and waits for <paramref name="exited"/>; past the deadline, or when the ask itself fails,
	/// calls <paramref name="force"/> once and waits for the exit without bound (a killed child exits).
	/// </summary>
	public static async Task<ProcessStopOutcome> RunAsync(ProcessStopPlan plan, Task exited, Action force, ISupervisorClock clock) {
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(exited);
		ArgumentNullException.ThrowIfNull(force);
		ArgumentNullException.ThrowIfNull(clock);
		using var deadline = new CancellationTokenSource();
		var ask = AskAsync(plan, deadline.Token);
		var timer = clock.Delay(plan.Deadline, deadline.Token);
		string? forced = null;
		while (forced is null && !exited.IsCompleted) {
			var done = await Task.WhenAny(exited, timer, ask).ConfigureAwait(false);
			if (done == timer) {
				forced = $"didn't exit within {plan.Deadline.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s of {plan.Description}";
			} else if (done == ask) {
				forced = await ask.ConfigureAwait(false);
				ask = Unanswered;
			}
		}
		await deadline.CancelAsync().ConfigureAwait(false);
		if (forced is null || exited.IsCompleted) {
			await exited.ConfigureAwait(false);
			return ProcessStopOutcome.Graceful;
		}
		force();
		await exited.ConfigureAwait(false);
		return ProcessStopOutcome.Forced(forced);
	}

	/// <summary>Runs an owner's own synchronous teardown, which has no ask to escalate from.</summary>
	public static Task<ProcessStopOutcome> Immediately(Action stop) {
		ArgumentNullException.ThrowIfNull(stop);
		stop();
		return Task.FromResult(ProcessStopOutcome.Graceful);
	}

	// Null once asked (or the child is already gone); otherwise why the child could not be asked at all.
	private static async Task<string?> AskAsync(ProcessStopPlan plan, CancellationToken ct) {
		try {
			await plan.Ask(ct).ConfigureAwait(false);
			return null;
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException
			|| (ex is OperationCanceledException && ct.IsCancellationRequested)) {
			return null;
		} catch (Exception ex) {
			return $"couldn't be asked to stop ({plan.Description} failed: {ex.Message})";
		}
	}
}
