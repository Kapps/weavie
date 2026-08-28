using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.WorktreeServe.Tests;

public sealed class PreviewStateBootstrapTests : IDisposable {
	private readonly string _root = Directory.CreateTempSubdirectory("preview-state-bootstrap-tests-").FullName;
	private readonly LocalFileSystem _fileSystem = new();

	[Fact]
	public void Refresh_projects_exact_session_provider_and_only_loads_the_current_checkout() {
		string production = Directory.CreateDirectory(Path.Combine(_root, "production")).FullName;
		string preview = Directory.CreateDirectory(Path.Combine(_root, "preview")).FullName;
		string workspace = Directory.CreateDirectory(Path.Combine(_root, "repository")).FullName;
		string current = Directory.CreateDirectory(Path.Combine(_root, "current-worktree")).FullName;
		string other = Directory.CreateDirectory(Path.Combine(_root, "other-worktree")).FullName;
		var workspaceId = WorkspaceId.ForPath(workspace);
		WriteSessions(production, workspaceId, [
			Descriptor("current", "current", current, loaded: false, "codex-acp"),
			Descriptor("other", "other", other, loaded: true, "claude"),
		]);
		WriteInstalledAgents(production, "codex-acp");
		Write(Under(production, WeaviePaths.SettingsFile), "[agent]\ndefaultProvider = \"codex-acp\"\n");
		Write(Under(production, WeaviePaths.KeybindingsFile), "[]");
		Write(Under(production, WeaviePaths.ThemeOverridesFile), "{}");
		Write(Under(production, WeaviePaths.AcpControlsFile), "{}");
		Write(Under(production, WeaviePaths.WorkspaceSettingsFile(workspaceId)), "[test]\nprofile = \"all\"\n");
		Write(Under(production, WeaviePaths.WorkspaceLayoutFile(workspaceId)), "{}");
		Write(Path.Combine(Under(production, WeaviePaths.Themes), "custom.json"), "{}");

		string previewOwned = Path.Combine(Under(preview, WeaviePaths.WorkspaceWorktreesDir(workspaceId)), "preview");
		WriteSessions(preview, workspaceId, [
			Descriptor("preview", "preview", previewOwned, loaded: true, "claude"),
		]);
		Write(Under(preview, WeaviePaths.AcpSessionsFile), "preview-conversations");
		Write(Under(preview, WeaviePaths.WorkspaceWorktreesFile(workspaceId)), "preview-worktree-registry");
		Write(Under(production, WeaviePaths.AcpSessionsFile), "production-conversations");
		Write(Under(production, WeaviePaths.WorkspaceWorktreesFile(workspaceId)), "production-worktree-registry");

		var result = PreviewStateBootstrap.Refresh(production, preview, workspace, current);

		Assert.Equal("current", result.SelectedSession.Value);
		Assert.Equal("codex-acp", result.SelectedProvider);
		var sessions = SessionStore.ReadSnapshot(
			_fileSystem,
			Under(preview, WeaviePaths.WorkspaceSessionsFile(workspaceId)));
		Assert.True(sessions.Items.Single(session => session.Id.Value == "current").Loaded);
		Assert.False(sessions.Items.Single(session => session.Id.Value == "other").Loaded);
		Assert.False(sessions.Items.Single(session => session.Id.Value == "preview").Loaded);
		Assert.Equal("codex-acp", sessions.Items.Single(session => session.Id.Value == "current").AgentProviderId);
		Assert.Equal(
			"[agent]\ndefaultProvider = \"codex-acp\"\n",
			File.ReadAllText(Under(preview, WeaviePaths.SettingsFile)));
		Assert.True(File.Exists(Path.Combine(Under(preview, WeaviePaths.Themes), "custom.json")));
		Assert.Equal("preview-conversations", File.ReadAllText(Under(preview, WeaviePaths.AcpSessionsFile)));
		Assert.Equal(
			"preview-worktree-registry",
			File.ReadAllText(Under(preview, WeaviePaths.WorkspaceWorktreesFile(workspaceId))));
		var rail = new RailStateStore(_fileSystem, Under(preview, WeaviePaths.RailStateFile));
		Assert.Equal(("local", "current"), rail.Selected);
		Assert.Empty(rail.Promoted);
	}

	[Fact]
	public void Refresh_requires_an_exact_production_session_before_writing_preview_configuration() {
		string production = Directory.CreateDirectory(Path.Combine(_root, "production")).FullName;
		string preview = Directory.CreateDirectory(Path.Combine(_root, "preview")).FullName;
		string workspace = Directory.CreateDirectory(Path.Combine(_root, "repository")).FullName;
		string current = Directory.CreateDirectory(Path.Combine(_root, "current-worktree")).FullName;
		var workspaceId = WorkspaceId.ForPath(workspace);
		WriteSessions(production, workspaceId, [
			Descriptor("other", "other", Path.Combine(_root, "other"), loaded: true, "claude"),
		]);
		Write(Under(production, WeaviePaths.SettingsFile), "production");
		Write(Under(preview, WeaviePaths.SettingsFile), "preview");

		Assert.Throws<InvalidOperationException>(
			() => PreviewStateBootstrap.Refresh(production, preview, workspace, current));

		Assert.Equal("preview", File.ReadAllText(Under(preview, WeaviePaths.SettingsFile)));
	}

	[Fact]
	public void Refresh_rejects_an_unavailable_provider() {
		string production = Directory.CreateDirectory(Path.Combine(_root, "production")).FullName;
		string preview = Directory.CreateDirectory(Path.Combine(_root, "preview")).FullName;
		string workspace = Directory.CreateDirectory(Path.Combine(_root, "repository")).FullName;
		string current = Directory.CreateDirectory(Path.Combine(_root, "current-worktree")).FullName;
		var workspaceId = WorkspaceId.ForPath(workspace);
		WriteSessions(production, workspaceId, [
			Descriptor("current", "current", current, loaded: true, "missing-acp"),
		]);

		var error = Assert.Throws<InvalidOperationException>(
			() => PreviewStateBootstrap.Refresh(production, preview, workspace, current));

		Assert.Contains("unavailable provider 'missing-acp'", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Refresh_strictly_rejects_malformed_production_sessions_without_repairing_them() {
		string production = Directory.CreateDirectory(Path.Combine(_root, "production")).FullName;
		string preview = Directory.CreateDirectory(Path.Combine(_root, "preview")).FullName;
		string workspace = Directory.CreateDirectory(Path.Combine(_root, "repository")).FullName;
		string current = Directory.CreateDirectory(Path.Combine(_root, "current-worktree")).FullName;
		var workspaceId = WorkspaceId.ForPath(workspace);
		string sessionsPath = Under(production, WeaviePaths.WorkspaceSessionsFile(workspaceId));
		Write(sessionsPath, "{ broken ");

		Assert.Throws<JsonException>(
			() => PreviewStateBootstrap.Refresh(production, preview, workspace, current));

		Assert.Equal("{ broken ", File.ReadAllText(sessionsPath));
		Assert.False(File.Exists(sessionsPath + ".bad"));
	}

	private void WriteSessions(
		string root,
		WorkspaceId workspaceId,
		IReadOnlyList<SessionDescriptor> sessions) =>
		SessionStore.WriteSnapshot(
			_fileSystem,
			Under(root, WeaviePaths.WorkspaceSessionsFile(workspaceId)),
			new SessionStoreSnapshot { Items = sessions, ShellColumns = 120, ShellRows = 40 });

	private static SessionDescriptor Descriptor(
		string id,
		string label,
		string worktree,
		bool loaded,
		string provider) => new() {
			Id = new SessionId(id),
			Label = label,
			WorktreePath = worktree,
			Loaded = loaded,
			AgentProviderId = provider,
			EditorSession = EditorSession.Empty,
		};

	private static void WriteInstalledAgents(string root, string id) => Write(
		Under(root, WeaviePaths.AcpInstallationsFile),
		$$"""
		{"version":1,"agents":[{"id":"{{id}}","name":"Codex","version":"1.0.0","command":"npx","arguments":["--yes","codex-acp@1.0.0"],"environment":{},"distribution":"npx"}]}
		""");

	private static string Under(string root, string canonicalPath) =>
		Path.Combine(root, Path.GetRelativePath(WeaviePaths.Root, canonicalPath));

	private static void Write(string path, string contents) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, contents);
	}

	public void Dispose() => Directory.Delete(_root, recursive: true);
}
