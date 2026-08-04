using System.Text.Json;
using Weavie.Core.FileActivity;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostSessionFileActivityTests {
	[Fact]
	public async Task FileWrite_ResponseArrivesBeforeBufferSaveConsumersSettle() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var subscription = session.FileActivity.Subscribe(
			"blocked test consumer",
			async fact => {
				if (fact is BufferSaved) {
					entered.TrySetResult();
					await release.Task;
				}
			},
			_ => Task.CompletedTask);
		string path = Path.Combine(host.RepoRoot, "save.cs");

		var response = host.SessionRequestAsync<JsonElement>(
			session,
			"files",
			"write",
			new { path, content = "saved\n" });
		await entered.Task;

		Assert.True(response.IsCompleted, "the file response must be delivered before activity consumers settle");
		release.SetResult();
		Assert.True((await response).GetProperty("ok").GetBoolean());
		await session.FileActivity.DrainAsync(CancellationToken.None);
	}

	[Fact]
	public async Task FailedFileWrite_ReportsNoBufferSave() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		int saves = 0;
		using var subscription = session.FileActivity.Subscribe(
			"capture test consumer",
			fact => {
				if (fact is BufferSaved) {
					saves++;
				}
				return Task.CompletedTask;
			},
			_ => Task.CompletedTask);

		var response = await host.SessionRequestAsync<JsonElement>(
			session,
			"files",
			"write",
			new { path = Path.Combine(host.RepoRoot, "..", "outside.cs"), content = "no\n" });
		await session.FileActivity.DrainAsync(CancellationToken.None);

		Assert.False(response.GetProperty("ok").GetBoolean());
		Assert.Equal(0, saves);
	}

	[Fact]
	public async Task CorrectionPublicationFailure_DoesNotSuppressSaveActivity() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "agent\n");
		session.Changes.RecordChange(path);
		await session.FileActivity.DrainAsync(CancellationToken.None);
		session.Changes.Corrected += _ => throw new InvalidOperationException("correction failed");
		int saves = 0;
		using var subscription = session.FileActivity.Subscribe(
			"capture save",
			fact => {
				if (fact is BufferSaved) {
					saves++;
				}
				return Task.CompletedTask;
			},
			_ => Task.CompletedTask);

		var response = await host.SessionRequestAsync<JsonElement>(
			session,
			"files",
			"write",
			new { path, content = "user\n" });
		await session.Bus.DrainAsync();
		await session.FileActivity.DrainAsync(CancellationToken.None);

		Assert.True(response.GetProperty("ok").GetBoolean());
		Assert.Equal(1, saves);
		var notification = Assert.NotNull(
			host.Bridge.LastEvent(session.Address, "notifications", "show"));
		Assert.Contains("Couldn't record your correction", notification.GetProperty("message").GetString());
	}

	[Fact]
	public async Task SessionDispose_WaitsForAdmittedFileActivity() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var subscription = session.FileActivity.Subscribe(
			"blocked teardown consumer",
			async fact => {
				if (fact is BufferSaved) {
					entered.TrySetResult();
					await release.Task;
				}
			},
			_ => Task.CompletedTask);
		var response = host.SessionRequestAsync<JsonElement>(
			session,
			"files",
			"write",
			new { path = Path.Combine(host.RepoRoot, "dispose.cs"), content = "saved\n" });
		await entered.Task;
		await response;

		var dispose = session.DisposeAsync().AsTask();
		await Task.Yield();
		Assert.False(dispose.IsCompleted);
		release.SetResult();

		await dispose;
	}
}
