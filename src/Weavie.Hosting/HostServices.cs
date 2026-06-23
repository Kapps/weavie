using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Remote;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;

namespace Weavie.Hosting;

/// <summary>
/// The app-global Core stores a <see cref="HostCore"/> drives: settings, the command catalog + keybindings, and
/// theme overrides. Most hosts use <see cref="CreateDefault"/>; a multi-window host shares one instance across
/// windows, so they're passed in rather than owned by the core.
/// </summary>
public sealed record HostServices {
	/// <summary>User settings (<c>~/.weavie/settings.toml</c>) — the change hub the host reacts to.</summary>
	public required SettingsStore Settings { get; init; }

	/// <summary>The command catalog (built-in commands, including the session commands).</summary>
	public required CommandRegistry CommandRegistry { get; init; }

	/// <summary>The user keybindings resolved over the defaults (<c>~/.weavie/keybindings.json</c>).</summary>
	public required KeybindingStore Keybindings { get; init; }

	/// <summary>Per-theme colour overrides (<c>~/.weavie/theme-overrides.json</c>).</summary>
	public required ThemeOverridesStore ThemeOverrides { get; init; }

	/// <summary>
	/// The Claude-session-id map (<c>~/.weavie/claude-sessions.json</c>), keyed by working directory — app-global
	/// so every window/session resumes its own directory's previous Claude conversation across launches.
	/// </summary>
	public required ClaudeSessionStore ClaudeSessions { get; init; }

	/// <summary>
	/// The registered remote agents (<c>~/.weavie/remote-agents.json</c>) — app-global so every window shares
	/// one registry and re-pushes it to its page on change.
	/// </summary>
	public required RemoteAgentStore RemoteAgents { get; init; }

	/// <summary>
	/// The session rail's UI state (<c>~/.weavie/rail-state.json</c>) — last-used backend + promoted remote
	/// sessions; app-global so every window shares it and re-pushes it to its page on change.
	/// </summary>
	public required RailStateStore RailState { get; init; }

	/// <summary>
	/// Builds the standard single-process store set — settings + keybindings watched live, console logging
	/// wired — for hosts that own exactly one workspace per process (Mac/Linux/Headless).
	/// </summary>
	public static HostServices CreateDefault() {
		var settings = CoreSettings.CreateStore(filePath: null, enableWatcher: true);
		settings.Log += Log;
		var registry = CoreCommands.CreateRegistry();
		var keybindings = new KeybindingStore(registry, filePath: null, enableWatcher: true);
		keybindings.Log += Log;
		var themeOverrides = new ThemeOverridesStore(new LocalFileSystem(), path: null);
		themeOverrides.Log += Log;
		var claudeSessions = new ClaudeSessionStore(new LocalFileSystem(), WeaviePaths.ClaudeSessionsFile);
		claudeSessions.Log += Log;
		var remoteAgents = new RemoteAgentStore(new LocalFileSystem(), path: null);
		remoteAgents.Log += Log;
		var railState = new RailStateStore(new LocalFileSystem(), path: null);
		railState.Log += Log;
		return new HostServices {
			Settings = settings,
			CommandRegistry = registry,
			Keybindings = keybindings,
			ThemeOverrides = themeOverrides,
			ClaudeSessions = claudeSessions,
			RemoteAgents = remoteAgents,
			RailState = railState,
		};
	}

	private static void Log(string line) {
		Console.WriteLine(line);
		Console.Out.Flush();
	}
}
