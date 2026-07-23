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
		var changes = new SessionChangeTracker(fs, root, _ => true, NoopReviewCheckpointStore.Instance);
		var router = new AgentEventRouter(changes, new ObservedPermissionMode(), new SessionStatusMachine());

		router.Observe(new AgentToolStarting(new AgentMutation.File("app.cs", Cwd: null, ProvidesEditLocation: true)));
		fs.WriteAllText(Path.Combine(root, "app.cs"), "one\nTWO\n");
		var feedback = router.Observe(new AgentToolCompleted(new AgentMutation.File("app.cs", Cwd: null, ProvidesEditLocation: true)));

		Assert.Equal(["app.cs:2"], feedback.Messages);
	}

	[Fact]
	public void Observe_MultiFileMutationCompletion_ReturnsAllEditLocationFeedback() {
		var fs = new InMemoryFileSystem();
		string root = Path.Combine(Path.GetTempPath(), "weavie-router-multifile-test");
		fs.WriteAllText(Path.Combine(root, "a.cs"), "one\ntwo\n");
		fs.WriteAllText(Path.Combine(root, "b.cs"), "red\nblue\n");
		var changes = new SessionChangeTracker(fs, root, _ => true, NoopReviewCheckpointStore.Instance);
		var router = new AgentEventRouter(changes, new ObservedPermissionMode(), new SessionStatusMachine());
		var mutation = new AgentMutation.Files([
			new AgentMutation.File("a.cs", Cwd: null, ProvidesEditLocation: true),
			new AgentMutation.File("b.cs", Cwd: null, ProvidesEditLocation: true),
		]);

		router.Observe(new AgentToolStarting(mutation));
		fs.WriteAllText(Path.Combine(root, "a.cs"), "one\nTWO\n");
		fs.WriteAllText(Path.Combine(root, "b.cs"), "red\nBLUE\n");
		var feedback = router.Observe(new AgentToolCompleted(mutation));

		Assert.Equal(["a.cs:2", "b.cs:2"], feedback.Messages);
	}
}
