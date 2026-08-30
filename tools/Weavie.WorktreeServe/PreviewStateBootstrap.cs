using Weavie.AcpDistribution;
using Weavie.Core;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Weavie.Core.Workspaces;

namespace Weavie.WorktreeServe;

internal sealed record PreviewStateBootstrapResult(
	SessionId SelectedSession,
	string SelectedProvider,
	int SessionCount);

internal static class PreviewStateBootstrap {
	public static PreviewStateBootstrapResult Refresh(
		string productionRoot,
		string previewRoot,
		string workspaceRoot,
		string selectedWorktree) {
		string source = Path.GetFullPath(productionRoot);
		string destination = Path.GetFullPath(previewRoot);
		var workspaceId = WorkspaceId.ForPath(workspaceRoot);
		string sourceSessionsPath = Under(source, WeaviePaths.WorkspaceSessionsFile(workspaceId));
		string destinationSessionsPath = Under(destination, WeaviePaths.WorkspaceSessionsFile(workspaceId));
		var productionSessions = SessionStore.ReadSnapshot(new LocalFileSystem(), sourceSessionsPath);
		var selected = productionSessions.Items.Where(session =>
			PhysicalPath.Equal(session.WorktreePath, selectedWorktree)).ToArray();
		if (selected.Length != 1) {
			throw new InvalidOperationException(
				$"production state has {selected.Length} sessions for the checkout '{selectedWorktree}'; expected exactly one.");
		}

		var catalog = AcpDistributionSnapshot.ReadCatalog(source);
		string provider = selected[0].AgentProviderId;
		if (provider != "claude" && catalog.All(agent => agent.Id != provider)) {
			throw new InvalidOperationException(
				$"session '{selected[0].Label}' uses unavailable provider '{provider}' in production state.");
		}

		var projected = productionSessions.Items.Select(session => session with {
			Loaded = PhysicalPath.Equal(session.WorktreePath, selectedWorktree),
		}).ToList();
		PreservePreviewOwnedSessions(destination, workspaceId, destinationSessionsPath, projected);

		foreach (string canonicalPath in UserConfigurationFiles()) {
			FileTreeSnapshot.MirrorFile(Under(source, canonicalPath), Under(destination, canonicalPath), destination);
		}
		FileTreeSnapshot.MirrorDirectory(
			Under(source, WeaviePaths.Themes),
			Under(destination, WeaviePaths.Themes),
			destination);
		foreach (string canonicalPath in WorkspaceConfigurationFiles(workspaceId)) {
			FileTreeSnapshot.MirrorFile(Under(source, canonicalPath), Under(destination, canonicalPath), destination);
		}
		_ = AcpDistributionSnapshot.Materialize(source, destination);

		SessionStore.WriteSnapshot(new LocalFileSystem(), destinationSessionsPath, new SessionStoreSnapshot {
			Items = projected,
			ShellColumns = productionSessions.ShellColumns,
			ShellRows = productionSessions.ShellRows,
		});
		var rail = new RailStateStore(new LocalFileSystem(), Under(destination, WeaviePaths.RailStateFile));
		rail.SetLastLocation("local");
		rail.SetPromoted([]);
		rail.SetSelected("local", selected[0].Id.Value);
		return new PreviewStateBootstrapResult(selected[0].Id, provider, projected.Count);
	}

	private static void PreservePreviewOwnedSessions(
		string previewRoot,
		WorkspaceId workspaceId,
		string sessionsPath,
		List<SessionDescriptor> projected) {
		if (!File.Exists(sessionsPath)) {
			return;
		}
		string previewWorktrees = Under(previewRoot, WeaviePaths.WorkspaceWorktreesDir(workspaceId));
		var existing = SessionStore.ReadSnapshot(new LocalFileSystem(), sessionsPath);
		foreach (var session in existing.Items.Where(session =>
			PhysicalPath.IsSameOrDescendant(session.WorktreePath, previewWorktrees)
			&& !PhysicalPath.Equal(session.WorktreePath, previewWorktrees))) {
			if (projected.Any(candidate => candidate.Id == session.Id
				|| PhysicalPath.Equal(candidate.WorktreePath, session.WorktreePath))) {
				continue;
			}
			projected.Add(session with { Loaded = false });
		}
	}

	private static IReadOnlyList<string> UserConfigurationFiles() => [
		WeaviePaths.SettingsFile,
		WeaviePaths.KeybindingsFile,
		WeaviePaths.ThemeOverridesFile,
		WeaviePaths.AcpControlsFile,
	];

	private static IReadOnlyList<string> WorkspaceConfigurationFiles(WorkspaceId workspaceId) => [
		WeaviePaths.WorkspaceSettingsFile(workspaceId),
		WeaviePaths.WorkspaceLayoutFile(workspaceId),
	];

	private static string Under(string root, string canonicalPath) =>
		Path.Combine(root, Path.GetRelativePath(WeaviePaths.Root, canonicalPath));
}
