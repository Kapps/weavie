using Weavie.Core;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Diagnostics;
using Weavie.Core.FileSystem;
using Weavie.Core.Remote;
using Weavie.Core.Review;
using Weavie.Core.Search;
using Weavie.Core.Sessions;
using Weavie.Core.Sources;
using Weavie.Core.Spelling;
using Weavie.Core.Suggestions;
using Weavie.Core.Theming;
using Weavie.Hosting.Agents.Claude;
using Weavie.Hosting.Agents.Codex;

namespace Weavie.Hosting;

/// <summary>
/// The app-global Core stores a <see cref="HostCore"/> drives: settings, the command catalog + keybindings, and
/// theme overrides. Most hosts use <see cref="CreateDefault"/>; a multi-window host shares one instance across
/// windows, so they're passed in rather than owned by the core.
/// </summary>
public sealed record HostServices : IDisposable {
	/// <summary>User settings (<c>~/.weavie/settings.toml</c>) — the change hub the host reacts to.</summary>
	public required SettingsStore Settings { get; init; }

	/// <summary>The command catalog (built-in commands, including the session commands).</summary>
	public required CommandRegistry CommandRegistry { get; init; }

	/// <summary>The contextual-suggestion catalog (built-in nudges the per-workspace service evaluates).</summary>
	public required SuggestionRegistry SuggestionRegistry { get; init; }

	/// <summary>The user keybindings resolved over the defaults (<c>~/.weavie/keybindings.json</c>).</summary>
	public required KeybindingStore Keybindings { get; init; }

	/// <summary>Per-theme colour overrides (<c>~/.weavie/theme-overrides.json</c>).</summary>
	public required ThemeOverridesStore ThemeOverrides { get; init; }

	/// <summary>The required embedded-agent provider catalog.</summary>
	public required AgentProviderRegistry AgentProviders { get; init; }

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
	/// The find-in-files panel's UI state (<c>~/.weavie/search-state.json</c>) — match options, include/exclude
	/// globs, recent search terms; app-global so every window shares it and re-pushes it to its page on change.
	/// </summary>
	public required SearchStateStore SearchState { get; init; }

	/// <summary>
	/// Lists open pull requests for the Open-PR flow. The default talks to GitHub; the headless harness swaps a
	/// <see cref="StaticPullRequestProvider"/> so a PR journey is deterministic and offline.
	/// </summary>
	public required IPullRequestProvider PullRequests { get; init; }

	/// <summary>Loads/posts a PR's review comments (same GitHub client as <see cref="PullRequests"/>, or the harness stub).</summary>
	public required IReviewCommentStore ReviewComments { get; init; }

	/// <summary>
	/// The source system (Notion, …): validates the user's pasted access token and fetches docs. The default talks
	/// to the real Notion API; the headless harness injects a deterministic stand-in for the connect/fetch journey.
	/// </summary>
	public required ISourceConnector Sources { get; init; }

	/// <summary>The process's captured console output (stdout/stderr teed into a bounded ring), backing the in-app log viewer.</summary>
	public required LogBuffer LogBuffer { get; init; }

	/// <summary>The immutable embedded Hunspell catalogs shared by every workspace in this host process.</summary>
	public required SpellCatalog SpellingCatalog { get; init; }

	/// <summary>The app-global custom dictionary (<c>~/.weavie/dictionary.txt</c>), shared by every workspace in this host process.</summary>
	public required CustomDictionary UserDictionary { get; init; }

	/// <summary>
	/// Builds the standard single-process store set — settings + keybindings watched live, console logging
	/// wired — for hosts that own exactly one workspace per process (Mac/Linux/Headless).
	/// </summary>
	public static HostServices CreateDefault() {
		// Install the console tee first so every store's construction log below lands in the in-app log viewer too.
		var logBuffer = LogBuffer.InstallConsoleCapture();
		var settings = CoreSettings.CreateStore(filePath: null, enableWatcher: true);
		settings.Log += Log;
		var registry = CoreCommands.CreateRegistry();
		var suggestions = CoreSuggestions.CreateRegistry();
		var keybindings = new KeybindingStore(registry, filePath: null, enableWatcher: true);
		keybindings.Log += Log;
		var themeOverrides = new ThemeOverridesStore(new LocalFileSystem(), path: null);
		themeOverrides.Log += Log;
		var claudeSessions = new ClaudeSessionStore(new LocalFileSystem(), WeaviePaths.ClaudeSessionsFile);
		claudeSessions.Log += Log;
		var agentProviders = new AgentProviderRegistry();
		agentProviders.Register(new ClaudeAgentProvider(claudeSessions));
		agentProviders.Register(new CodexAgentProvider(
			new CodexThreadStore(new LocalFileSystem(), WeaviePaths.CodexThreadsFile)));
		var remoteAgents = new RemoteAgentStore(new LocalFileSystem(), path: null);
		remoteAgents.Log += Log;
		var railState = new RailStateStore(new LocalFileSystem(), path: null);
		railState.Log += Log;
		var searchState = new SearchStateStore(new LocalFileSystem(), path: null);
		searchState.Log += Log;
		var spellingCatalog = SpellCatalog.LoadEmbedded();
		var userDictionary = new CustomDictionary(WeaviePaths.UserDictionaryFile, enableWatcher: true);
		var github = new GitHubReviewProvider(http: null, new GitHubTokenSource());
		return new HostServices {
			Settings = settings,
			CommandRegistry = registry,
			SuggestionRegistry = suggestions,
			Keybindings = keybindings,
			ThemeOverrides = themeOverrides,
			AgentProviders = agentProviders,
			RemoteAgents = remoteAgents,
			RailState = railState,
			SearchState = searchState,
			PullRequests = github,
			ReviewComments = github,
			Sources = SourceConnector.CreateDefault(),
			LogBuffer = logBuffer,
			SpellingCatalog = spellingCatalog,
			UserDictionary = userDictionary,
		};
	}

	/// <summary>Disposes the app-global watchers owned by this service set after every host window has closed.</summary>
	public void Dispose() {
		UserDictionary.Dispose();
		Keybindings.Dispose();
		Settings.Dispose();
	}

	private static void Log(string line) {
		Console.WriteLine(line);
		Console.Out.Flush();
	}
}
