using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;
using Weavie.Core.Sessions;
using Weavie.Hosting.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AgentEventRouterTests {
	[Fact]
	public void Observe_FileMutationCompletion_ReturnsEditLocationFeedback() {
		var fs = new InMemoryFileSystem();
		string root = Path.Combine(Path.GetTempPath(), "weavie-router-test");
		fs.WriteAllText(Path.Combine(root, "app.cs"), "one\ntwo\n");
		var changes = new SessionChangeTracker(fs, root, _ => true);
		var router = new AgentEventRouter(changes, new ObservedPermissionMode(), new SessionStatusMachine());

		router.Observe(new AgentToolStarting(new AgentMutation.File("app.cs", Cwd: null, ProvidesEditLocation: true)));
		fs.WriteAllText(Path.Combine(root, "app.cs"), "one\nTWO\n");
		var feedback = router.Observe(new AgentToolCompleted(new AgentMutation.File("app.cs", Cwd: null, ProvidesEditLocation: true)));

		Assert.Equal(["app.cs:2"], feedback.Messages);
	}

	[Fact]
	public void Observe_WorkspaceMutationCompletion_RecordsChangedFiles() {
		var fs = new InMemoryFileSystem();
		string root = Path.Combine(Path.GetTempPath(), "weavie-router-workspace-test");
		string file = Path.Combine(root, "app.cs");
		fs.WriteAllText(file, "one\ntwo\n");
		var changes = new SessionChangeTracker(fs, root, _ => true);
		var router = new AgentEventRouter(changes, new ObservedPermissionMode(), new SessionStatusMachine());

		router.Observe(new AgentToolStarting(new AgentMutation.Workspace()));
		fs.WriteAllText(file, "one\nTWO\n");
		router.Observe(new AgentToolCompleted(new AgentMutation.Workspace()));

		var turn = Assert.Single(changes.TurnChanges());
		Assert.Equal("one\ntwo\n", turn.BaselineText);
		Assert.Equal("one\nTWO\n", turn.CurrentText);
	}
}
