using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <c>worktree.setupCommand</c> is workspace-scoped and lives in the out-of-repo overlay; the provisioner must
/// resolve it against the workspace root, or a rootless resolve reads empty and the command silently never runs
/// on worktree create.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreWorktreeCommandTests {
	[Fact]
	public async Task SetupCommand_FromWorkspaceOverlay_RunsOnWorktreeCreate() {
		await using var host = await TestHost.StartAsync(repo => {
			string overlay = WeaviePaths.WorkspaceSettingsFile(WorkspaceId.ForPath(repo));
			Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
			File.WriteAllText(overlay, "worktree.setupCommand = 'echo weavie-setup-ran'\n");
		});

		await host.CreateSessionAsync("feature");

		// The "ready" toast fires only when a command actually ran; a rootless resolve would read empty and be a
		// silent Ran=false no-op. So its arrival proves the provisioner read the overlay with the workspace root.
		var ready = await Wait.ForAsync(() => {
			var n = host.Bridge.LastOfType("notify");
			return n is { } v && (v.GetProperty("message").GetString() ?? string.Empty)
				.Contains("is ready", StringComparison.Ordinal)
				? v
				: (JsonElement?)null;
		});
		Assert.Equal("info", ready.GetProperty("level").GetString());
	}
}
