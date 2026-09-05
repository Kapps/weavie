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
		// Terminal disposal blocks until the PTY children exit (so a following worktree delete can't race a
		// process still rooted there). Keep it off the calling UI thread.
		await DisposeStepAsync(failures, "shell processes", () => Task.Run(() => Shells.Dispose())).ConfigureAwait(false);
		await DisposeStepAsync(failures, "agent", () => Agent.DisposeAsync().AsTask()).ConfigureAwait(false);
		await DisposeStepAsync(
			failures, "file activity drain",
			() => FileActivity.DrainAsync(CancellationToken.None)).ConfigureAwait(false);
		await DisposeStepAsync(failures, "external file tracking", () => Task.Run(ExternalFiles.Dispose)).ConfigureAwait(false);
		await DisposeStepAsync(failures, "file activity", () => FileActivity.DisposeAsync().AsTask()).ConfigureAwait(false);
		await DisposeStepAsync(failures, "file opener", () => FileOpener.DisposeAsync().AsTask()).ConfigureAwait(false);
		await DisposeStepAsync(failures, "language servers", () => Lsp.DisposeAsync().AsTask()).ConfigureAwait(false);
		await DisposeStepAsync(failures, "pasted images", () => {
			PastedImages.Clear();
			return Task.CompletedTask;
		}).ConfigureAwait(false);
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
			failures.Add(ex);
		}
	}
}
