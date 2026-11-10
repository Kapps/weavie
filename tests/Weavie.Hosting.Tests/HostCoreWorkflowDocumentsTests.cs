using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Workspaces;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Sessions;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCoreWorkflowDocumentsTests {
	[Fact]
	public async Task NewForkedAndWorkflowSessionsShareDocumentsAcrossReloadAndParentDeletion() {
		await using var host = await HostCoreWorkflowTests.StartAsync(new HostCoreWorkflowTests.DecisionStub("yes", 0.9));
		var contexts = new Dictionary<string, IAgentTurnContext>();
		var provider = Assert.IsType<FakeStructuredAgentProvider>(host.AgentProviders.RequireAvailable("structured"));
		provider.CreatingSession = context => contexts[context.Workspace] = context.TurnContext;
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "parent",
			Base = "main",
			AgentProviderId = "structured",
			Workflow = "flow",
		})).Ok);
		var parent = host.Session("parent");
		var run = parent.Workflow.Current!;
		string documents = run.DocumentsDirectory;
		Assert.True(Directory.Exists(documents));
		Assert.False(documents.StartsWith(parent.WorkspaceRoot, StringComparison.Ordinal));
		string path = Path.Combine(documents, "monitoring.md");
		File.WriteAllText(path, "parent\n");

		Assert.True((await host.InvokeCommandAsync("parent", SessionCommands.NewSession,
			new { branch = "plain", @base = "main", agentProviderId = "structured", prompt = "read shared documents" }, CancellationToken.None)).Ok);
		Assert.True((await host.InvokeCommandAsync("parent", SessionCommands.NewSession,
			new { branch = "child", @base = "main", agentProviderId = "structured", workflow = "flow" }, CancellationToken.None)).Ok);
		Assert.True((await host.InvokeCommandAsync("plain", SessionCommands.ForkSession,
			new { branch = "grandchild", handoff = "continue monitoring" }, CancellationToken.None)).Ok);
		Assert.Equal(documents, host.Session("child").Workflow.Current!.DocumentsDirectory);
		Assert.Contains(documents, contexts[host.Session("plain").WorkspaceRoot].Compose());
		Assert.Contains(documents, contexts[host.Session("grandchild").WorkspaceRoot].Compose());

		Assert.True((await host.UnloadSessionAsync("plain")).Ok);
		Assert.True((await host.InvokeCommandAsync("child", SessionCommands.LoadSession, new { id = "plain" }, CancellationToken.None)).Ok);
		Assert.Contains(documents, contexts[host.Session("plain").WorkspaceRoot].Compose());
		Assert.True((await host.InvokeCommandAsync("plain", CoreCommands.WorkflowRun, new { workflow = "flow" }, CancellationToken.None)).Ok);
		Assert.Equal(documents, host.Session("plain").Workflow.Current!.DocumentsDirectory);

		Assert.True((await host.UnloadSessionAsync("grandchild")).Ok);
		await host.RestartAsync();
		parent = host.Session("parent");
		Assert.Equal(documents, host.Session("plain").Workflow.Current!.DocumentsDirectory);
		Assert.True((await host.InvokeCommandAsync("child", SessionCommands.LoadSession, new { id = "grandchild" }, CancellationToken.None)).Ok);
		Assert.Equal(documents, host.Session("grandchild").InheritedWorkflowDocuments);

		parent.Workflow.Cancel(run.Id);
		host.SelectWorkspaceSession();
		Assert.True((await host.DeleteSessionAsync("parent", force: true, classify: false)).Ok);
		File.AppendAllText(Path.Combine(host.Session("child").Workflow.Current!.DocumentsDirectory, "monitoring.md"), "child\n");
		Assert.Equal("parent\nchild\n", File.ReadAllText(path));
		foreach (string child in new[] { "child", "plain", "grandchild" }) {
			Assert.True((await host.DeleteSessionAsync(child, force: true, classify: false)).Ok);
		}
		Assert.False(Directory.Exists(documents));
	}

	[Theory]
	[InlineData("run")]
	[InlineData("sessions")]
	public async Task CorruptOwnershipStopsStartupCleanup(string corrupt) {
		await using var host = await HostCoreWorkflowTests.StartAsync(new HostCoreWorkflowTests.DecisionStub("yes", 0.9));
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "owner", Base = "main", AgentProviderId = "structured", Workflow = "flow",
		})).Ok);
		var owner = host.Session("owner");
		string documents = owner.Workflow.Current!.DocumentsDirectory;
		string state = corrupt == "run"
			? WeaviePaths.WorkspaceWorkflowRunFile(WorkspaceId.ForPath(owner.WorkspaceRoot))
			: WeaviePaths.WorkspaceSessionsFile(WorkspaceId.ForPath(host.RepoRoot));
		await host.RestartAsync(() => File.WriteAllText(state, "{}"));
		Assert.True(Directory.Exists(documents));
	}

	[Fact]
	public async Task IndependentRunsGetDistinctDirectoriesAndReloadPreservesTheCurrentOne() {
		await using var host = await HostCoreWorkflowTests.StartAsync(new HostCoreWorkflowTests.DecisionStub("yes", 0.9));
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "root",
			Base = "main",
			AgentProviderId = "structured",
			Workflow = "flow",
		})).Ok);
		var runner = host.Session("root").Workflow;
		var first = runner.Current!;
		runner.Cancel(first.Id);
		var second = runner.Start("flow", JsonDocument.Parse("{}").RootElement);
		Assert.NotEqual(first.DocumentsDirectory, second.DocumentsDirectory);
		Assert.False(Directory.Exists(first.DocumentsDirectory));
		Assert.True((await host.UnloadSessionAsync("root")).Ok);
		Assert.True((await host.InvokeCommandAsync(host.WorkspaceSession.SlotId, SessionCommands.LoadSession,
			new { id = "root" }, CancellationToken.None)).Ok);
		Assert.Equal(second.DocumentsDirectory, host.Session("root").Workflow.Current!.DocumentsDirectory);
	}
}
