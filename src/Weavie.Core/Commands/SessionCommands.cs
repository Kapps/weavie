using System.Globalization;
using System.Text.Json;
using Weavie.Core.Sessions;

namespace Weavie.Core.Commands;

/// <summary>
/// Declares the multi-session commands and wires the Core-handled ones (new / fork / unload / delete) to a
/// host's <see cref="ISessionHost"/>; the switch commands (next / prev / switch) run in the web (the rail).
/// Declarations live in Core so every trigger sees them. See <c>docs/specs/multi-session-and-worktrees.md</c>.
/// </summary>
public static class SessionCommands {
	/// <summary>Creates a new session on its own worktree + branch (args <c>branch</c>/<c>base</c>/<c>prompt</c>); the programmatic entry. The interactive UI uses <see cref="NewSessionPrompt"/>.</summary>
	public const string NewSession = "weavie.session.new";

	/// <summary>Opens the interactive new-session prompt (branch name + base) in the UI; <c>$mod+Shift+n</c>.</summary>
	public const string NewSessionPrompt = "weavie.session.newPrompt";

	/// <summary>Opens the pull-request picker (check out a PR's branch as a session) in the UI; <c>$mod+Shift+r</c>.</summary>
	public const string OpenPr = "weavie.pr.open";

	/// <summary>Forks the current session into a new worktree off its HEAD (args <c>branch</c>/<c>handoff</c>).</summary>
	public const string ForkSession = "weavie.session.fork";

	/// <summary>Switches to the next session on the rail; <c>ctrl+Tab</c> whenever the editor isn't focused.</summary>
	public const string NextSession = "weavie.session.next";

	/// <summary>Switches to the previous session on the rail; <c>ctrl+Shift+Tab</c> whenever the editor isn't focused.</summary>
	public const string PrevSession = "weavie.session.prev";

	/// <summary>Opens the omnibar to pick a session to switch to.</summary>
	public const string SwitchSession = "weavie.session.switch";

	/// <summary>Switches to the Nth session on the rail (1-based); bound to <c>ctrl+Shift+1..9</c>, dispatched with <c>{ "index": N }</c>.</summary>
	public const string SelectSessionByIndex = "weavie.session.selectByIndex";

	/// <summary>Loads a dormant session's backend in the background (arg <c>id</c>) without switching the page to it.</summary>
	public const string LoadSession = "weavie.session.load";

	/// <summary>Unloads a session (the active one, or the <c>id</c> arg) into a dormant chip, keeping its worktree on disk.</summary>
	public const string UnloadSession = "weavie.session.unload";

	/// <summary>Deletes a session: removes its worktree (keeps the branch), guarded against uncommitted changes unless <c>force</c>. The programmatic entry; the UI uses <see cref="DeleteSessionPrompt"/>.</summary>
	public const string DeleteSession = "weavie.session.delete";

	/// <summary>Opens the interactive delete confirmation in the UI (arg <c>id</c>; defaults to the active session).</summary>
	public const string DeleteSessionPrompt = "weavie.session.deletePrompt";

	/// <summary>Disconnects + forgets a registered remote agent by <c>agent</c> (its name); web-handled, no Core handler.</summary>
	public const string DisconnectRemote = "weavie.session.disconnectRemote";

	/// <summary>Removes a promoted remote session from the rail's working set (args <c>backendId</c>/<c>id</c>); web-handled, no Core handler.</summary>
	public const string RemoveFromRail = "weavie.session.removeFromRail";

	/// <summary>Registers the session command definitions into <paramref name="registry"/>.</summary>
	public static void Register(CommandRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new CommandDefinition {
			Id = NewSession,
			Title = "New Session",
			RunsIn = CommandLocation.Core,
			Category = "Session",
			Description = "Create a new session on its own git worktree + branch. With no 'branch' the host "
				+ "auto-names one (avoiding existing branches). 'base' is 'current' (the active session's HEAD; the "
				+ "default) or 'main'. Set 'existing' true to instead check out an existing branch named by 'branch' "
				+ "(no new branch; 'base' is ignored), switching to that session if one already exists. An optional "
				+ "'prompt' is sent to the new session's Claude as its first message. This is the programmatic entry "
				+ "(for Claude); the interactive UI uses 'New Session…' (weavie.session.newPrompt).",
			Aliases = ["new session", "create session", "new worktree", "branch session", "new agent", "another claude", "spin up a session", "check out branch", "open existing branch"],
			// Hidden from the palette: the human-facing entry is the interactive prompt (NewSessionPrompt). Still
			// reachable by Claude via listCommands/runCommand.
			ShowInPalette = false,
			ArgsSchemaJson = "{\"branch\":{\"type\":\"string\"},\"base\":{\"type\":\"string\",\"enum\":[\"current\",\"main\"]},\"existing\":{\"type\":\"boolean\"},\"prompt\":{\"type\":\"string\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = NewSessionPrompt,
			Title = "New Session…",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Open the new-session prompt: name a branch, then branch off the current session's HEAD "
				+ "(Enter) or main (Shift+Enter). The interactive counterpart of weavie.session.new.",
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+n" }],
		});

		registry.Register(new CommandDefinition {
			Id = OpenPr,
			Title = "Open Pull Request…",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Open one of the repository's open pull requests as a session checked out on its head "
				+ "branch, seeding Claude with the PR's context.",
			Aliases = ["open pr", "open pull request", "review pr", "check out pr", "open github pr", "pull request"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+r" }],
		});

		registry.Register(new CommandDefinition {
			Id = ForkSession,
			Title = "Fork Session",
			RunsIn = CommandLocation.Core,
			Category = "Session",
			Description = "Fork the current session into a new worktree branched off its HEAD, carrying a handoff "
				+ "brief to the new session's Claude. With no 'branch' the host derives one. 'handoff' is the "
				+ "summary/instruction seeded as the fork's first message.",
			Aliases = ["fork session", "branch this", "spin off", "fork this conversation", "branch off here", "try this in a branch"],
			ArgsSchemaJson = "{\"branch\":{\"type\":\"string\"},\"handoff\":{\"type\":\"string\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = NextSession,
			Title = "Next Session",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Switch to the next session on the rail (wraps around).",
			Aliases = ["next session", "switch to next session"],
			// ctrl+Tab cycles sessions whenever the editor isn't focused — the exact complement of the editor's
			// editorFocused next-tab binding, so the two never both match. !editorFocused (not terminalFocused)
			// also fires on load, before a non-auto-focusing terminal has ever set terminalFocused. Literal ctrl
			// (not $mod): Cmd+Tab is the OS app switcher. A per-binding guard keeps the command in the palette.
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+Tab", When = "!editorFocused" }],
		});

		registry.Register(new CommandDefinition {
			Id = PrevSession,
			Title = "Previous Session",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Switch to the previous session on the rail (wraps around).",
			Aliases = ["previous session", "prev session", "switch to previous session"],
			// Mirror of NextSession: ctrl+Shift+Tab cycles backward whenever the editor isn't focused.
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+Shift+Tab", When = "!editorFocused" }],
		});

		registry.Register(new CommandDefinition {
			Id = SwitchSession,
			Title = "Switch Session…",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Open the omnibar to pick a session to switch to.",
			Aliases = ["switch session", "go to session", "change session", "pick session"],
		});

		// ctrl+Shift+1..9 → switch to the Nth session. Literal ctrl (not $mod) to stay Ctrl on macOS, where
		// Cmd+Shift+3/4/5 are screenshot shortcuts. Keybinding-only; each binding carries its own index argument.
		var indexBindings = new List<CommandKeybinding>(9);
		for (int i = 1; i <= 9; i++) {
			string n = i.ToString(CultureInfo.InvariantCulture);
			indexBindings.Add(new CommandKeybinding { Key = $"ctrl+Shift+{n}", ArgsJson = $"{{\"index\":{n}}}" });
		}

		registry.Register(new CommandDefinition {
			Id = SelectSessionByIndex,
			Title = "Switch to Session by Number",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Switch to the Nth session on the rail (1-based, in rail order).",
			Aliases = ["switch to session", "select session", "go to session number"],
			DefaultKeybindings = indexBindings,
			ShowInPalette = false,
			ArgsSchemaJson = "{\"index\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"1-based session number in rail order\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = LoadSession,
			Title = "Load Session",
			RunsIn = CommandLocation.Core,
			Category = "Session",
			Description = "Load a dormant session's backend (Claude / terminals / LSP) in the background, by 'id', "
				+ "WITHOUT switching the page to it — so its Claude runs and reports status while you stay where you "
				+ "are. Use Switch Session to bring it to the foreground instead.",
			Aliases = ["load session", "start session", "wake session", "resume session in background"],
			// id-targeted (a specific dormant chip); loading the active session is meaningless, so not in the palette.
			ShowInPalette = false,
			ArgsSchemaJson = "{\"id\":{\"type\":\"string\",\"description\":\"Session id to load in the background\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = UnloadSession,
			Title = "Unload Session",
			RunsIn = CommandLocation.Core,
			Category = "Session",
			Description = "Unload a session (the active one, or the session 'id') into a dormant chip: tear its live "
				+ "backend (Claude / terminals / LSP) down but keep its worktree on disk so it can be reloaded later. "
				+ "Dormant chips sort to the bottom of the rail and are skipped when cycling. To remove the worktree "
				+ "entirely, use Delete Session.",
			Aliases = ["unload session", "park session", "make session dormant", "suspend session"],
			ArgsSchemaJson = "{\"id\":{\"type\":\"string\",\"description\":\"Session id to unload; omit for the active session\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = DeleteSession,
			Title = "Delete Session",
			RunsIn = CommandLocation.Core,
			Category = "Session",
			Description = "Delete a session (the active one, or the session 'id'): remove its git worktree but KEEP the "
				+ "branch (committed work survives on it). Refuses when the worktree has uncommitted changes unless "
				+ "'force' is true, so work is never discarded silently. The primary session can't be deleted. With "
				+ "'classify' true it deletes nothing and instead returns the worktree's state (clean/untracked/modified) "
				+ "for a confirm prompt. This is the programmatic entry (for Claude); the interactive UI uses 'Delete "
				+ "Session…' (weavie.session.deletePrompt).",
			Aliases = ["delete session", "remove session", "delete worktree", "remove worktree", "discard session"],
			// The human-facing entry is the guarded prompt (DeleteSessionPrompt); the raw delete stays reachable by Claude.
			ShowInPalette = false,
			ArgsSchemaJson = "{\"id\":{\"type\":\"string\",\"description\":\"Session id to delete; omit for the active session\"},"
				+ "\"force\":{\"type\":\"boolean\",\"description\":\"Delete even if the worktree has uncommitted changes\"},"
				+ "\"classify\":{\"type\":\"boolean\",\"description\":\"Don't delete; return the worktree state {state,label} for a confirm prompt\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = DeleteSessionPrompt,
			Title = "Delete Session…",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Open the delete confirmation for a session ('id', or the active session): it classifies the "
				+ "worktree and escalates the confirm when there are untracked files or uncommitted changes. The "
				+ "interactive counterpart of weavie.session.delete.",
			Aliases = ["delete session", "remove session", "delete worktree"],
			ArgsSchemaJson = "{\"id\":{\"type\":\"string\",\"description\":\"Session id to delete; omit for the active session\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = DisconnectRemote,
			Title = "Disconnect Remote Agent",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Disconnect a registered remote agent by 'agent' (its name): close its bridge, drop its "
				+ "sessions from the rail, and forget it from this client's saved agents. Local sessions are unaffected. "
				+ "Web-handled (the agent registry is client-side), targeted from the rail's right-click menu.",
			Aliases = ["disconnect remote agent", "remove remote agent", "forget remote agent", "disconnect agent", "remove agent"],
			// Target-specific (which agent) with no meaningful no-arg palette row, like the id-targeted session ops.
			ShowInPalette = false,
			ArgsSchemaJson = "{\"agent\":{\"type\":\"string\",\"description\":\"Name of the registered remote agent to disconnect\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = RemoveFromRail,
			Title = "Remove from Rail",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Remove a promoted remote session (by 'backendId' + 'id') from the rail's working set. The "
				+ "session keeps running on its remote box and stays available in the cloud panel; this only drops it "
				+ "from your rail. Web-handled (the working set is client-side).",
			Aliases = ["remove from rail", "drop from rail", "demote session", "remove remote session from rail"],
			ShowInPalette = false,
			ArgsSchemaJson = "{\"backendId\":{\"type\":\"string\"},\"id\":{\"type\":\"string\"}}",
		});
	}

	/// <summary>
	/// Registers the Core-handled session commands onto <paramref name="dispatcher"/>, routing each to
	/// <paramref name="host"/>. Returns a disposable that unregisters them all.
	/// </summary>
	public static IDisposable RegisterHandlers(CommandDispatcher dispatcher, ISessionHost host) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(host);

		var registrations = new List<IDisposable> {
			dispatcher.RegisterHandler(NewSession, (argsJson, ct) => host.NewSessionAsync(
				new NewSessionRequest {
					Branch = GetString(argsJson, "branch"),
					Base = GetString(argsJson, "base"),
					Prompt = GetString(argsJson, "prompt"),
					AttachExisting = GetBool(argsJson, "existing"),
				},
				ct)),
			dispatcher.RegisterHandler(ForkSession, (argsJson, ct) => host.ForkSessionAsync(
				new ForkSessionRequest {
					Branch = GetString(argsJson, "branch"),
					Handoff = GetString(argsJson, "handoff"),
				},
				ct)),
			dispatcher.RegisterHandler(LoadSession, (argsJson, ct) => host.LoadSessionAsync(GetString(argsJson, "id"), ct)),
			dispatcher.RegisterHandler(UnloadSession, (argsJson, ct) => host.UnloadSessionAsync(GetString(argsJson, "id"), ct)),
			dispatcher.RegisterHandler(DeleteSession, (argsJson, ct) => GetBool(argsJson, "classify")
				? host.ClassifyDeleteAsync(GetString(argsJson, "id"), ct)
				: host.DeleteSessionAsync(GetString(argsJson, "id"), GetBool(argsJson, "force"), ct)),
		};

		return new CompositeDisposable(registrations);
	}

	private static string? GetString(string? argsJson, string name) {
		if (string.IsNullOrWhiteSpace(argsJson)) {
			return null;
		}

		try {
			using var doc = JsonDocument.Parse(argsJson);
			if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(name, out var prop)) {
				return null;
			}

			return prop.ValueKind switch {
				JsonValueKind.String => prop.GetString(),
				JsonValueKind.Null => null,
				_ => prop.GetRawText(),
			};
		} catch (JsonException) {
			return null;
		}
	}

	private static bool GetBool(string? argsJson, string name) {
		if (string.IsNullOrWhiteSpace(argsJson)) {
			return false;
		}

		try {
			using var doc = JsonDocument.Parse(argsJson);
			if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(name, out var prop)) {
				return false;
			}

			return prop.ValueKind switch {
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				// Embedded Claude sends scalars as JSON strings ("true"/"1"); coerce leniently at the boundary.
				JsonValueKind.String => bool.TryParse(prop.GetString(), out bool b) ? b : prop.GetString() == "1",
				JsonValueKind.Number => prop.TryGetInt64(out long n) && n != 0,
				_ => false,
			};
		} catch (JsonException) {
			return false;
		}
	}

	private sealed class CompositeDisposable : IDisposable {
		private readonly List<IDisposable> _items;

		public CompositeDisposable(List<IDisposable> items) {
			_items = items;
		}

		public void Dispose() {
			foreach (var item in _items) {
				item.Dispose();
			}
		}
	}
}
