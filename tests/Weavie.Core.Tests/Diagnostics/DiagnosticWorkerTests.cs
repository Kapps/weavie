using System.Collections.Concurrent;
using Weavie.Core.Diagnostics;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class DiagnosticWorkerTests {
	[Fact]
	public async Task BlockedSinkUsesOneOrderedWorkerAndReportsCoalescing() {
		var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var summaryReported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		var messages = new ConcurrentQueue<string>();
		int active = 0;
		int maxActive = 0;
		var worker = new DiagnosticWorker(message => {
			int current = Interlocked.Increment(ref active);
			InterlockedExtensions.Max(ref maxActive, current);
			try {
				messages.Enqueue(message);
				if (message == "first") {
					firstEntered.TrySetResult();
					releaseFirst.Task.GetAwaiter().GetResult();
				} else if (message.StartsWith("Coalesced ", StringComparison.Ordinal)) {
					summaryReported.TrySetResult(message);
				}
			} finally {
				Interlocked.Decrement(ref active);
			}
		});

		worker.Report("first");
		await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		for (int i = 0; i < 300; i++) {
			worker.Report($"message-{i}");
		}

		releaseFirst.TrySetResult();
		string summary = await summaryReported.Task.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Equal(1, Volatile.Read(ref maxActive));
		Assert.Contains("Coalesced 44 diagnostics", summary, StringComparison.Ordinal);
		Assert.Contains("latest: message-299", summary, StringComparison.Ordinal);
		Assert.Equal(258, messages.Count);
	}

	private static class InterlockedExtensions {
		public static void Max(ref int target, int value) {
			int current;
			do {
				current = Volatile.Read(ref target);
				if (current >= value) {
					return;
				}
			} while (Interlocked.CompareExchange(ref target, value, current) != current);
		}
	}
}
