using Weavie.Core.Workspaces;

namespace Weavie.Core;

/// <summary>
/// Single source of truth for Weavie's on-disk locations. Every subsystem — settings, themes, and
/// host-internal caches — resolves its path from here so nothing hardcodes its own. All Weavie data
/// lives under the cross-platform Weavie root, <c>~/.weavie</c>.
/// </summary>
public static class WeaviePaths {
	/// <summary>The Weavie root — the user's home directory plus <c>.weavie</c> (e.g. <c>~/.weavie</c>).</summary>
	public static string Root { get; } =
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weavie");

	/// <summary>Where user settings live: <c>~/.weavie/settings</c>.</summary>
	public static string Settings { get; } = Path.Combine(Root, "settings");

	/// <summary>The user settings file: <c>~/.weavie/settings.toml</c>.</summary>
	public static string SettingsFile { get; } = Path.Combine(Root, "settings.toml");

	/// <summary>The user keybindings file: <c>~/.weavie/keybindings.json</c> (a list of {key, command, args?, when?} records).</summary>
	public static string KeybindingsFile { get; } = Path.Combine(Root, "keybindings.json");

	/// <summary>The legacy single-window layout (pane tree + window geometry): <c>~/.weavie/layout.json</c>. Superseded by per-workspace layout files; kept for the default store and back-compat.</summary>
	public static string LayoutFile { get; } = Path.Combine(Root, "layout.json");

	/// <summary>Root for per-workspace state (each workspace's layout + window geometry): <c>~/.weavie/workspaces</c>.</summary>
	public static string Workspaces { get; } = Path.Combine(Root, "workspaces");

	/// <summary>The persisted most-recently-opened workspace list: <c>~/.weavie/recents.json</c>.</summary>
	public static string RecentsFile { get; } = Path.Combine(Root, "recents.json");

	/// <summary>
	/// The Claude session id Weavie assigned to each working directory: <c>~/.weavie/claude-sessions.json</c>.
	/// Lets a reopened session resume its previous Claude conversation instead of cold-starting one. App-global
	/// (keyed by directory, which is per-session) rather than per-workspace, so any host shares one map. See
	/// <see cref="Sessions.ClaudeSessionStore"/>.
	/// </summary>
	public static string ClaudeSessionsFile { get; } = Path.Combine(Root, "claude-sessions.json");

	/// <summary>The per-theme color overrides document (spec §6): <c>~/.weavie/theme-overrides.json</c>. Its own file, like layout — never part of settings.toml.</summary>
	public static string ThemeOverridesFile { get; } = Path.Combine(Root, "theme-overrides.json");

	/// <summary>Where installed and built-in themes live: <c>~/.weavie/themes</c>.</summary>
	public static string Themes { get; } = Path.Combine(Root, "themes");

	/// <summary>Root for host-internal caches (e.g. the WebView2 user-data folder): <c>~/.weavie/internals</c>.</summary>
	public static string Internals { get; } = Path.Combine(Root, "internals");

	/// <summary>
	/// Resolves a named host-internal cache folder under <see cref="Internals"/>,
	/// e.g. <c>Internal("webview2")</c> → <c>~/.weavie/internals/webview2</c>.
	/// </summary>
	/// <param name="name">The cache folder name.</param>
	/// <returns>The absolute path to that folder under <see cref="Internals"/>.</returns>
	public static string Internal(string name) => Path.Combine(Internals, name);

	/// <summary>
	/// This workspace's state folder, keyed by its <see cref="WorkspaceId"/>:
	/// <c>~/.weavie/workspaces/&lt;id&gt;</c>.
	/// </summary>
	/// <param name="id">The workspace identity (a path-derived digest).</param>
	/// <returns>The absolute path to that workspace's state folder.</returns>
	public static string WorkspaceDir(WorkspaceId id) => Path.Combine(Workspaces, id.Value);

	/// <summary>
	/// This workspace's pane layout + window geometry:
	/// <c>~/.weavie/workspaces/&lt;id&gt;/layout.json</c>.
	/// </summary>
	/// <param name="id">The workspace identity (a path-derived digest).</param>
	/// <returns>The absolute path to that workspace's layout file.</returns>
	public static string WorkspaceLayoutFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "layout.json");

	/// <summary>
	/// This workspace's persisted editor session (open files + per-file Monaco view state):
	/// <c>~/.weavie/workspaces/&lt;id&gt;/editor-session.json</c>.
	/// </summary>
	/// <param name="id">The workspace identity (a path-derived digest).</param>
	/// <returns>The absolute path to that workspace's editor-session file.</returns>
	public static string WorkspaceEditorSessionFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "editor-session.json");

	/// <summary>
	/// This workspace's scratch (untitled-buffer) directory:
	/// <c>~/.weavie/workspaces/&lt;id&gt;/scratch</c>. New files (<c>Ctrl+N</c>) are backed by a real temp file
	/// here — outside the workspace, so they never reach the file tree, the index, git, or Claude — until the
	/// user saves them under a real name. See <see cref="Editor.ScratchStore"/>.
	/// </summary>
	/// <param name="id">The workspace identity (a path-derived digest).</param>
	/// <returns>The absolute path to that workspace's scratch directory.</returns>
	public static string WorkspaceScratchDir(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "scratch");

	/// <summary>
	/// Where this workspace's per-session git worktrees live: <c>~/.weavie/workspaces/&lt;id&gt;/worktrees</c>.
	/// Kept under the workspace's state folder (outside the repo) so worktree directories never appear in
	/// the user's project tree, and so cleanup is scoped per workspace.
	/// </summary>
	/// <param name="id">The workspace identity (a path-derived digest).</param>
	/// <returns>The absolute path to that workspace's worktrees directory.</returns>
	public static string WorkspaceWorktreesDir(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "worktrees");

	/// <summary>
	/// The registry of worktrees Weavie created for this workspace:
	/// <c>~/.weavie/workspaces/&lt;id&gt;/worktrees.json</c>. The backbone of the "no leaked worktrees"
	/// guarantee — reconciled against <c>git worktree list</c> on every load. See <see cref="Worktrees.WorktreeRegistry"/>.
	/// </summary>
	/// <param name="id">The workspace identity (a path-derived digest).</param>
	/// <returns>The absolute path to that workspace's worktree registry file.</returns>
	public static string WorkspaceWorktreesFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "worktrees.json");

	/// <summary>
	/// This workspace's persisted session set (the sessions and which was active):
	/// <c>~/.weavie/workspaces/&lt;id&gt;/sessions.json</c>. Lets a workspace reopen with the same sessions
	/// bound to the same worktrees. See <see cref="Sessions.SessionStore"/>.
	/// </summary>
	/// <param name="id">The workspace identity (a path-derived digest).</param>
	/// <returns>The absolute path to that workspace's session-set file.</returns>
	public static string WorkspaceSessionsFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "sessions.json");
}
