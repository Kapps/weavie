using System.Diagnostics;
using System.Globalization;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed partial class HostCoreWorkflowConversationTests {

	[Fact]
	public async Task UnloadStillReleasesEveryOwnedResourceWhenPausingTheWorkflowCannotBeSaved() {
		await using var fixture = await ConversationFixture.StartAsync(Review.Replace("CONTEXT", "fork", StringComparison.Ordinal));
		string child = await fixture.StartReviewAsync();
		var session = fixture.Session;
		var execution = session.Workflow.Execution!;
		var childReply = await fixture.ReplyAsync(child, "echo: review the change");
		using var process = OwnedProcess("conversation-acp", Thread(childReply));
		Assert.False(process.HasExited);
		string runFile = WeaviePaths.WorkspaceWorkflowRunFile(WorkspaceId.ForPath(session.WorkspaceRoot));
		File.Delete(runFile);
		Directory.CreateDirectory(runFile);
		try {
			var error = await Assert.ThrowsAsync<AggregateException>(() => fixture.Host.InvokeCommandAsync(fixture.Host.WorkspaceSession.SlotId,
				SessionCommands.UnloadSession, new { id = "conversation" }, CancellationToken.None));
			Assert.Contains("Could not save workflow run", error.Message, StringComparison.Ordinal);
			await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
			Assert.True(process.HasExited);
			Assert.True(session.Background.Stopping.IsCancellationRequested);
			Assert.True(execution.Retired.IsCompletedSuccessfully);
			Assert.False(await execution.Retired);
			Assert.Null(fixture.Host.Core.SessionForTest("conversation"));
		} finally {
			Directory.Delete(runFile);
		}

		Process OwnedProcess(string provider, string thread) => Process.GetProcessById(int.Parse(
			File.ReadAllText(Path.Combine(fixture.State(provider), thread + ".owner")), CultureInfo.InvariantCulture));
	}
}
