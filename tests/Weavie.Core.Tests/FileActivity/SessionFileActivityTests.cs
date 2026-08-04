using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class SessionFileActivityTests {
	private static readonly FileStat Revision = new(true, false, 1, 1, 3);

	[Fact]
	public async Task MixedFacts_AreSequencedAndDeliveredInRegistrationOrder() {
		await using var activity = Create();
		var delivered = new List<string>();
		activity.Subscribe(
			"first",
			fact => {
				delivered.Add($"first:{fact.Sequence}:{fact.GetType().Name}");
				return Task.CompletedTask;
			},
			_ => Task.CompletedTask);
		activity.Subscribe(
			"second",
			fact => {
				delivered.Add($"second:{fact.Sequence}:{fact.GetType().Name}");
				return Task.CompletedTask;
			},
			_ => Task.CompletedTask);

		var saved = activity.ReportBufferSaved(PathFor("a.cs"), Revision);
		var changed = activity.ReportChanged(PathFor("b.cs"), Revision);
		var deleted = activity.ReportDeleted(PathFor("c.cs"));
		await activity.DrainAsync(CancellationToken.None);

		Assert.Equal(1, saved.Sequence);
		Assert.Equal(2, changed.Sequence);
		Assert.Equal(3, deleted.Sequence);
		Assert.Equal([
			"first:1:BufferSaved", "second:1:BufferSaved",
			"first:2:FileChanged", "second:2:FileChanged",
			"first:3:FileDeleted", "second:3:FileDeleted",
		], delivered);
	}

	[Fact]
	public async Task AdmissionSnapshotsSubscribers() {
		await using var activity = Create();
		var delivered = new List<string>();
		var first = activity.Subscribe(
			"first",
			fact => {
				delivered.Add($"first:{fact.Sequence}");
				return Task.CompletedTask;
			},
			_ => Task.CompletedTask);
		activity.ReportDeleted(PathFor("before.cs"));
		first.Dispose();
		activity.Subscribe(
			"second",
			fact => {
				delivered.Add($"second:{fact.Sequence}");
				return Task.CompletedTask;
			},
			_ => Task.CompletedTask);
		activity.ReportDeleted(PathFor("after.cs"));

		await activity.DrainAsync(CancellationToken.None);

		Assert.Equal(["first:1", "second:2"], delivered);
	}

	[Fact]
	public async Task SeparateSessions_HaveIndependentSequences() {
		await using var first = Create();
		await using var second = Create();

		var firstTicket = first.ReportDeleted(PathFor("first.cs"));
		var secondTicket = second.ReportDeleted(PathFor("second.cs"));

		Assert.Equal(1, firstTicket.Sequence);
		Assert.Equal(1, secondTicket.Sequence);
	}

	[Fact]
	public async Task ConsumerFailure_IsReportedAndLaterDeliveryContinues() {
		await using var activity = Create();
		var failures = new List<long>();
		var delivered = new List<long>();
		activity.Subscribe(
			"broken",
			_ => Task.FromException(new InvalidOperationException("broken")),
			failure => {
				failures.Add(failure.Fact.Sequence);
				return Task.CompletedTask;
			});
		activity.Subscribe(
			"healthy",
			fact => {
				delivered.Add(fact.Sequence);
				return Task.CompletedTask;
			},
			_ => Task.CompletedTask);

		var first = activity.ReportDeleted(PathFor("a.cs"));
		var second = activity.ReportDeleted(PathFor("b.cs"));
		await Task.WhenAll(first.Settled, second.Settled);

		Assert.Equal([1L, 2L], failures);
		Assert.Equal([1L, 2L], delivered);
	}

	[Fact]
	public async Task TicketAndDrain_WaitForBlockedConsumer() {
		await using var activity = Create();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		activity.Subscribe(
			"blocked",
			async _ => {
				entered.TrySetResult();
				await release.Task;
			},
			_ => Task.CompletedTask);

		var ticket = activity.ReportDeleted(PathFor("a.cs"));
		var drain = activity.DrainAsync(CancellationToken.None);
		await entered.Task;

		Assert.False(ticket.Settled.IsCompleted);
		Assert.False(drain.IsCompleted);
		release.SetResult();
		await Task.WhenAll(ticket.Settled, drain);
	}

	[Fact]
	public async Task FailureHandlerFault_FaultsTicketDrainAndDispose() {
		var activity = Create();
		activity.Subscribe(
			"broken",
			_ => Task.FromException(new InvalidOperationException("consumer")),
			_ => Task.FromException(new InvalidOperationException("handler")));

		var ticket = activity.ReportDeleted(PathFor("a.cs"));
		await Assert.ThrowsAsync<AggregateException>(() => ticket.Settled);
		await Assert.ThrowsAsync<AggregateException>(() => activity.DrainAsync(CancellationToken.None));
		await Assert.ThrowsAsync<AggregateException>(async () => await activity.DisposeAsync());
	}

	[Fact]
	public async Task Dispose_DrainsAndRejectsLateReports() {
		var activity = Create();
		var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		activity.Subscribe(
			"consumer",
			_ => {
				delivered.TrySetResult();
				return Task.CompletedTask;
			},
			_ => Task.CompletedTask);
		activity.ReportDeleted(PathFor("a.cs"));

		await activity.DisposeAsync();

		Assert.True(delivered.Task.IsCompletedSuccessfully);
		Assert.Throws<ObjectDisposedException>(() => activity.ReportDeleted(PathFor("late.cs")));
	}

	private static SessionFileActivity Create() => new(
		new WorkspaceInventory(
			Path.GetTempPath(),
			_ => Task.FromResult<IReadOnlyList<string>?>([])),
		_ => { },
		watcherDebounceMs: 25);

	private static string PathFor(string name) => Path.Combine(Path.GetTempPath(), name);
}
