using Weavie.Core.Workspaces;

namespace Weavie.Core;

/// <summary>
/// Single source of truth for Weavie's on-disk locations: every subsystem resolves its path from here so
/// nothing hardcodes its own. All Weavie data lives under the Weavie root, <c>~/.weavie</c>.
/// </summary>
public static class WeaviePaths {
	/// <summary>
	/// The Weavie root — the user's home directory plus <c>.weavie</c> (e.g. <c>~/.weavie</c>), unless
	/// <c>WEAVIE_ROOT</c> overrides it with an absolute path. The override is a bootstrap relocation seam (it can't
	/// be a setting — settings live under this root): the test harness points it at a throwaway dir so a run never
	/// reads or writes the developer's real config, which on Windows <c>$HOME</c> can't redirect (the user-profile
	/// known folder ignores it).
	/// </summary>
	public static string Root { get; } = ResolveRoot();

	private static string ResolveRoot() {
		string? overrideRoot = Environment.GetEnvironmentVariable("WEAVIE_ROOT");
		return string.IsNullOrEmpty(overrideRoot)
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weavie")
			: overrideRoot;
	}

	/// <summary>Where user settings live: <c>~/.weavie/settings</c>.</summary>
	public static string Settings { get; } = Path.Combine(Root, "settings");

	/// <summary>The user settings file: <c>~/.weavie/settings.toml</c>.</summary>
	public static string SettingsFile { get; } = Path.Combine(Root, "settings.toml");

	/// <summary>The user keybindings file: <c>~/.weavie/keybindings.json</c> (a list of {key, command, args?, when?} records).</summary>
	public static string KeybindingsFile { get; } = Path.Combine(Root, "keybindings.json");

	/// <summary>The single-window layout (pane tree + window geometry): <c>~/.weavie/layout.json</c>. Backs the default store; per-workspace layouts live under <see cref="Workspaces"/>.</summary>
	public static string LayoutFile { get; } = Path.Combine(Root, "layout.json");

	/// <summary>Root for per-workspace state (each workspace's layout + window geometry): <c>~/.weavie/workspaces</c>.</summary>
	public static string Workspaces { get; } = Path.Combine(Root, "workspaces");

	/// <summary>The persisted most-recently-opened workspace list: <c>~/.weavie/recents.json</c>.</summary>
	public static string RecentsFile { get; } = Path.Combine(Root, "recents.json");

	/// <summary>
	/// The Claude session id per working directory: <c>~/.weavie/claude-sessions.json</c>, so a reopened session
	/// resumes its prior conversation. App-global. See <see cref="Sessions.ClaudeSessionStore"/>.
	/// </summary>
	public static string ClaudeSessionsFile { get; } = Path.Combine(Root, "claude-sessions.json");

	/// <summary>
	/// The Codex app-server thread id per working directory: <c>~/.weavie/codex-threads.json</c>, so a reopened
	/// native Codex session resumes its prior thread. App-global. See <see cref="Sessions.CodexThreadStore"/>.
	/// </summary>
	public static string CodexThreadsFile { get; } = Path.Combine(Root, "codex-threads.json");

	/// <summary>
	/// The registered remote agents: <c>~/.weavie/remote-agents.json</c>. Its own file, never settings.toml — it
	/// holds bearer tokens, so it stays off the Claude-facing settings surface. See <see cref="Remote.RemoteAgentStore"/>.
	/// </summary>
	public static string RemoteAgentsFile { get; } = Path.Combine(Root, "remote-agents.json");

	/// <summary>
	/// The session rail's app-global UI state (last backend + promoted remote sessions):
	/// <c>~/.weavie/rail-state.json</c>. Runtime UI state, never settings.toml. See <see cref="Sessions.RailStateStore"/>.
	/// </summary>
	public static string RailStateFile { get; } = Path.Combine(Root, "rail-state.json");

	/// <summary>The per-theme color overrides document (spec §6): <c>~/.weavie/theme-overrides.json</c>. Its own file, never part of settings.toml.</summary>
	public static string ThemeOverridesFile { get; } = Path.Combine(Root, "theme-overrides.json");

	/// <summary>
	/// Where source (Notion, …) access tokens live: <c>~/.weavie/sources</c>. Holds personal access tokens, so it
	/// stays off the Claude-facing settings surface (like <see cref="RemoteAgentsFile"/>).
	/// </summary>
	public static string SourcesDir { get; } = Path.Combine(Root, "sources");

	/// <summary>A source's user-supplied access-token file: <c>~/.weavie/sources/&lt;sourceId&gt;.json</c> (<c>{ "token": "…" }</c>).</summary>
	public static string SourceCredentialsFile(string sourceId) => Path.Combine(SourcesDir, $"{sourceId}.json");

	/// <summary>Where installed and built-in themes live: <c>~/.weavie/themes</c>.</summary>
	public static string Themes { get; } = Path.Combine(Root, "themes");

	/// <summary>Root for host-internal caches (e.g. the WebView2 user-data folder): <c>~/.weavie/internals</c>.</summary>
	public static string Internals { get; } = Path.Combine(Root, "internals");

	/// <summary>Where host diagnostics and crash reports are written: <c>~/.weavie/logs</c>.</summary>
	public static string Logs { get; } = Path.Combine(Root, "logs");

	/// <summary>The live append target for unhandled-crash reports: <c>~/.weavie/logs/last-crash.log</c>.</summary>
	public static string LastCrashFile { get; } = Path.Combine(Logs, "last-crash.log");

	/// <summary>The prior run's crash report, rotated here once it's surfaced: <c>~/.weavie/logs/previous-crash.log</c>.</summary>
	public static string PreviousCrashFile { get; } = Path.Combine(Logs, "previous-crash.log");

	/// <summary>
	/// A named host-internal cache folder under <see cref="Internals"/>,
	/// e.g. <c>Internal("webview2")</c> → <c>~/.weavie/internals/webview2</c>.
	/// </summary>
	public static string Internal(string name) => Path.Combine(Internals, name);

	/// <summary>A workspace's state folder, keyed by its <see cref="WorkspaceId"/>: <c>~/.weavie/workspaces/&lt;id&gt;</c>.</summary>
	public static string WorkspaceDir(WorkspaceId id) => Path.Combine(Workspaces, id.Value);

	/// <summary>A workspace's pane layout + window geometry: <c>~/.weavie/workspaces/&lt;id&gt;/layout.json</c>.</summary>
	public static string WorkspaceLayoutFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "layout.json");

	/// <summary>A workspace's persisted editor session (open files + per-file Monaco view state): <c>~/.weavie/workspaces/&lt;id&gt;/editor-session.json</c>.</summary>
	public static string WorkspaceEditorSessionFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "editor-session.json");

	/// <summary>A workspace's frecency-ranked recent files (the omnibar's Recent section): <c>~/.weavie/workspaces/&lt;id&gt;/recent-files.json</c>.</summary>
	public static string WorkspaceRecentFilesFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "recent-files.json");

	/// <summary>
	/// A workspace's scratch (untitled-buffer) directory: <c>~/.weavie/workspaces/&lt;id&gt;/scratch</c>. New
	/// files live here outside the workspace, so they never reach the file tree, index, git, or Claude until
	/// saved under a real name. See <see cref="Editor.ScratchStore"/>.
	/// </summary>
	public static string WorkspaceScratchDir(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "scratch");

	/// <summary>
	/// A workspace's pasted-image directory: <c>~/.weavie/workspaces/&lt;id&gt;/pasted-images</c> (each session in
	/// its own worktree-keyed subdir). An image pasted into Claude is written here and its path injected into the
	/// prompt; the files never reach the tree, index, git, or a real name, and are wiped on session unload. See
	/// <see cref="Editor.PastedImageStore"/>.
	/// </summary>
	public static string WorkspacePastedImagesDir(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "pasted-images");

	/// <summary>
	/// A workspace's per-session git worktrees: <c>~/.weavie/workspaces/&lt;id&gt;/worktrees</c>. Outside the
	/// repo so they never appear in the project tree and cleanup stays scoped per workspace.
	/// </summary>
	public static string WorkspaceWorktreesDir(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "worktrees");

	/// <summary>
	/// The registry of worktrees Weavie created for a workspace: <c>~/.weavie/workspaces/&lt;id&gt;/worktrees.json</c>.
	/// Backbone of the "no leaked worktrees" guarantee, reconciled against <c>git worktree list</c> on every
	/// load. See <see cref="Worktrees.WorktreeRegistry"/>.
	/// </summary>
	public static string WorkspaceWorktreesFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "worktrees.json");

	/// <summary>
	/// The per-workspace record of suggestions dismissed forever ("don't ask again"):
	/// <c>~/.weavie/workspaces/&lt;id&gt;/suggestions.json</c>. See <see cref="Suggestions.SuggestionDismissals"/>.
	/// </summary>
	public static string WorkspaceSuggestionsFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "suggestions.json");

	/// <summary>
	/// A workspace's recorded post-turn corrections (JSONL ring, oldest first):
	/// <c>~/.weavie/workspaces/&lt;id&gt;/corrections.jsonl</c>. See <see cref="Corrections.CorrectionCorpus"/>.
	/// </summary>
	public static string WorkspaceCorrectionsFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "corrections.jsonl");

	/// <summary>
	/// A workspace's persisted session set (the sessions and which was active):
	/// <c>~/.weavie/workspaces/&lt;id&gt;/sessions.json</c>. Lets a workspace reopen with the same sessions
	/// bound to the same worktrees. See <see cref="Sessions.SessionStore"/>.
	/// </summary>
	public static string WorkspaceSessionsFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "sessions.json");

	/// <summary>
	/// A workspace's settings overlay: <c>~/.weavie/workspaces/&lt;id&gt;/settings.toml</c>. Workspace-scoped
	/// settings (test.profile, worktree.*) live here, outside the repo, so a cloned repo can't ship an auto-run
	/// setup command and configuring a workspace never touches source control.
	/// </summary>
	public static string WorkspaceSettingsFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "settings.toml");

	/// <summary>
	/// A workspace's per-session terminal scrollback logs: <c>~/.weavie/workspaces/&lt;id&gt;/terminal-logs</c>.
	/// One capped append log per (worktree, pane) lets a re-attaching client replay a coherent shell screen
	/// instead of a blank pane. See <see cref="Terminal.ScrollbackLog"/>.
	/// </summary>
	public static string WorkspaceTerminalLogsDir(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "terminal-logs");

	/// <summary>
	/// The scrollback log for one session's terminal pane:
	/// <c>~/.weavie/workspaces/&lt;id&gt;/terminal-logs/&lt;worktreeDigest&gt;-&lt;pane&gt;.log</c>. Keyed by a
	/// stable worktree-path digest (not the session's ephemeral id) so a worktree resumes the same log.
	/// </summary>
	/// <param name="id">The workspace whose terminal-logs directory holds the file.</param>
	/// <param name="worktreeDigest">A stable digest of the session's worktree path (e.g. <see cref="WorkspaceId.ForPath"/>).</param>
	/// <param name="pane">The terminal session tag (e.g. <c>shell</c>).</param>
	public static string WorkspaceTerminalLogFile(WorkspaceId id, string worktreeDigest, string pane) =>
		Path.Combine(WorkspaceTerminalLogsDir(id), $"{worktreeDigest}-{pane}.log");

	/// <summary>
	/// A workspace's per-session native agent pane transcripts:
	/// <c>~/.weavie/workspaces/&lt;id&gt;/agent-panes</c>. The structured (non-terminal) agent's analogue to the
	/// shell scrollback log — its rendered output persists here so the pane restores across reload/restart.
	/// </summary>
	public static string WorkspaceAgentPanesDir(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "agent-panes");

	/// <summary>
	/// The transcript for one session's native agent pane:
	/// <c>~/.weavie/workspaces/&lt;id&gt;/agent-panes/&lt;worktreeDigest&gt;.json</c>. Keyed by the stable
	/// worktree-path digest (not the session's ephemeral id) so a worktree resumes the same transcript.
	/// </summary>
	/// <param name="id">The workspace whose agent-panes directory holds the file.</param>
	/// <param name="worktreeDigest">A stable digest of the session's worktree path (e.g. <see cref="WorkspaceId.ForPath"/>).</param>
	public static string WorkspaceAgentPaneFile(WorkspaceId id, string worktreeDigest) =>
		Path.Combine(WorkspaceAgentPanesDir(id), $"{worktreeDigest}.json");
}
