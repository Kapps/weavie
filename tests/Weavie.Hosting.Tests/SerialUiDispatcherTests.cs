using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The guarantees HostCore's switch-race fixes lean on: actions run strictly in Post order even when posted
/// from many threads (a stale push can never overtake a switch), and a throwing action is reported without
/// killing the pump.
/// </summary>
public sealed class SerialUiDispatcherTests {
	[Fact]
	public async Task Post_RunsActionsInOrder_AcrossPostingThreads() {
		var dispatcher = new SerialUiDispatcher(_ => { });
		var ran = new List<int>();
		var done = new TaskCompletionSource();

		// Sequential Posts from racing threads: each thread posts its own index, but a barrier serializes the
		// Post calls themselves, so the run order must equal the post order.
		object gate = new();
		int next = 0;
		var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() => {
			for (int i = 0; i < 250; i++) {
				lock (gate) {
					int value = next++;
					dispatcher.Post(() => ran.Add(value));
				}
			}
		})).ToArray();
		foreach (var thread in threads) {
			thread.Start();
		}

		foreach (var thread in threads) {
			thread.Join();
		}

		dispatcher.Post(() => done.SetResult());
		await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(Enumerable.Range(0, 1000), ran);
	}

	[Fact]
	public async Task Post_AThrowingAction_IsReportedAndThePumpContinues() {
		var errors = new List<Exception>();
		var dispatcher = new SerialUiDispatcher(errors.Add);
		var done = new TaskCompletionSource();

		dispatcher.Post(() => throw new InvalidOperationException("boom"));
		dispatcher.Post(() => done.SetResult());

		await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal("boom", Assert.Single(errors).Message);
	}
}
