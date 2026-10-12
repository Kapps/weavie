using Weavie.Core.Processes;

namespace Weavie.Hosting;

public sealed partial class HostSession {

	/// <inheritdoc/>
	public ValueTask DisposeAsync() {
		lock (_disposeGate) {
			return new ValueTask(_disposeTask ??= DisposeCoreAsync());
		}
	}

	private async Task DisposeCoreAsync() {
		DiscardInitialInput();
		var failures = new List<Exception>();
		await DisposeStepAsync(failures, "message admission", () => _endpoint.QuiesceAsync()).ConfigureAwait(false);
		await DisposeStepAsync(failures, "file observation", FileActivity.StopObservingAsync).ConfigureAwait(false);
		await DisposeStepAsync(failures, "background tasks", () => Background.DisposeAsync().AsTask()).ConfigureAwait(false);
		// Every child gets its graceful-stop deadline at once, and each step completes only when its children have
		// exited, so a following worktree delete can't race a process still rooted there. PTY teardown blocks, so
		// each step starts off the calling UI thread.
		await Task.WhenAll(
			DisposeStepAsync(failures, "shell processes", () => Task.Run(() => Shells.DisposeAsync().AsTask())),
			DisposeStepAsync(failures, "agent", () => Task.Run(() => Agent.DisposeAsync().AsTask())),
			DisposeStepAsync(failures, "language servers", () => Task.Run(() => Lsp.DisposeAsync().AsTask()))).ConfigureAwait(false);
		await DisposeStepAsync(
			failures, "file activity drain",
			() => FileActivity.DrainAsync(CancellationToken.None)).ConfigureAwait(false);
		await DisposeStepAsync(failures, "observed paths", () => Task.Run(ObservedPaths.Dispose)).ConfigureAwait(false);
		await DisposeStepAsync(failures, "file activity", () => FileActivity.DisposeAsync().AsTask()).ConfigureAwait(false);
		await DisposeStepAsync(failures, "file opener", () => FileOpener.DisposeAsync().AsTask()).ConfigureAwait(false);
		await DisposeStepAsync(failures, "pasted images", () => {
			PastedImages.Clear();
			return Task.CompletedTask;
		}).ConfigureAwait(false);
		ProjectDictionary.Dispose();
		FileIndexGate.Dispose();
		await DisposeStepAsync(failures, "message endpoint", () => _endpoint.DisposeAsync().AsTask()).ConfigureAwait(false);
		if (failures.Count > 0) {
			throw new AggregateException(failures);
		}
	}

	private async Task DisposeStepAsync(List<Exception> failures, string resource, Func<Task> step) {
		Console.WriteLine($"[session:{SlotId}] disposing {resource}");
		try {
			await step().ConfigureAwait(false);
			Console.WriteLine($"[session:{SlotId}] disposed {resource}");
		} catch (Exception ex) {
			Console.WriteLine($"[session:{SlotId}] disposing {resource} failed: {ex}");
			lock (failures) failures.Add(ex);
		}
	}

	// A force-killed child may have stranded state (a .NET child's runtime IPC files); the journal keeps the
	// record when the stop happens at exit, where no toast is seen.
	private void ReportForceStopped(string name, ProcessStopOutcome outcome) {
		string message = $"{name} {outcome.ForcedBecause}; Weavie force-stopped it";
		Console.WriteLine($"[session:{SlotId}] {message}");
		ExitJournal.Note(message);
		Notify("warn", message);
	}
}
