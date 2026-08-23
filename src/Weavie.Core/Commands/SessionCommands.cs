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
	private const string NewSessionInputExecutionLane = "weavie.session.input";
	private const string LifecycleExecutionLane = "weavie.session.lifecycle";

	/// <summary>Creates a new session on its own worktree + branch (args <c>branch</c>/<c>base</c>/<c>prompt</c>); the programmatic entry.</summary>
	public const string NewSession = "weavie.session.new";

	/// <summary>Shows the shared Sessions UI; <c>$mod+Shift+n</c>.</summary>
	public const string ShowSessions = "weavie.session.show";

	/// <summary>Submits the focused new-session composer; <c>Shift+Enter</c>.</summary>
	public const string SubmitNewSession = "weavie.session.submitNew";

	/// <summary>Pastes the local clipboard into the focused new-session composer; <c>Ctrl+V</c> / <c>⌘V</c>.</summary>
	public const string PasteNewSession = "weavie.session.pasteNew";

	/// <summary>Re-runs the new-session composer's branch suggestion against the prompt as it now reads. Unbound.</summary>
	public const string ResuggestBranch = "weavie.session.resuggestBranch";

	/// <summary>Opens the pull-request picker (check out a PR's branch as a session) in the UI; <c>$mod+Shift+r</c>.</summary>
	public const string OpenPr = "weavie.pr.open";

	/// <summary>Opens the active branch's detected pull request in the browser; <c>$mod+Shift+g</c>.</summary>
	public const string OpenCurrentPr = "weavie.pr.openCurrent";

	/// <summary>Forks the invoking session into a new worktree off its HEAD (args <c>branch</c>/<c>handoff</c>).</summary>
	public const string ForkSession = "weavie.session.fork";

	/// <summary>Switches to the next session on the rail; <c>ctrl+Tab</c> whenever the editor isn't focused.</summary>
	public const string NextSession = "weavie.session.next";

	/// <summary>Switches to the previous session on the rail; <c>ctrl+Shift+Tab</c> whenever the editor isn't focused.</summary>
	public const string PrevSession = "weavie.session.prev";

	/// <summary>Opens the omnibar to pick a session to switch to.</summary>
	public const string SwitchSession = "weavie.session.switch";

	/// <summary>Focuses a session by <c>id</c> (+ optional <c>backendId</c>/<c>incarnation</c>); the notification click-through target. Web-handled.</summary>
	public const string FocusSession = "weavie.session.focus";

	/// <summary>Switches to the Nth session on the rail (1-based); bound to <c>ctrl+Shift+1..9</c>, dispatched with <c>{ "index": N }</c>.</summary>
	public const string SelectSessionByIndex = "weavie.session.selectByIndex";

	/// <summary>Loads a dormant session's backend in the background (arg <c>id</c>) without switching the page to it.</summary>
	public const string LoadSession = "weavie.session.load";

	/// <summary>Unloads the invoking session, or the <c>id</c> arg, into a dormant chip while keeping its worktree.</summary>
	public const string UnloadSession = "weavie.session.unload";

	/// <summary>Deletes the invoking session, or the <c>id</c> arg. Weavie-owned worktrees are removed; user-owned checkouts are preserved.</summary>
	public const string DeleteSession = "weavie.session.delete";

	/// <summary>Opens the interactive delete confirmation in the UI (arg <c>id</c>; defaults to the selected session).</summary>
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
			SharedExecutionLane = LifecycleExecutionLane,
			Scope = CommandScope.Host,
			Title = "New Session",
			RunsIn = CommandLocation.Core,
			Category = "Session",
			Description = "Create a new session on its own git worktree + branch. 'branch' is required. 'base' is 'source' (the invoking session's HEAD; the "
				+ "default) or 'main'. Set 'existing' true to instead check out an existing branch named by 'branch' "
				+ "(no new branch; 'base' is ignored), switching to that session if one already exists. An optional "
				+ "'prompt' and optional image 'attachments' are sent as the new session's first input. "
				+ "'agentProviderId' is any provider advertised by the current host; "
				+ "omitting it uses agent.defaultProvider. The interactive UI is the Sessions surface.",
			Aliases = ["new session", "create session", "new worktree", "branch session", "new agent", "another claude", "spin up a session", "check out branch", "open existing branch"],
			// Hidden from the palette: the human-facing entry is the Sessions surface. Still
			// reachable by Claude via listCommands/runCommand.
			ShowInPalette = false,
			ArgsSchemaJson = "{\"branch\":{\"type\":\"string\"},\"base\":{\"type\":\"string\",\"enum\":[\"source\",\"main\"]},\"existing\":{\"type\":\"boolean\"},\"prompt\":{\"type\":\"string\"},\"attachments\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"mime\":{\"type\":\"string\"},\"dataB64\":{\"type\":\"string\"}},\"required\":[\"id\",\"mime\",\"dataB64\"]}},\"agentProviderId\":{\"type\":\"string\",\"description\":\"Provider id advertised by the current host\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = ShowSessions,
			Title = "Sessions",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Show all sessions and the shared new-session composer.",
			Aliases = ["new session", "show sessions"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+n" }],
		});

		registry.Register(new CommandDefinition {
			Id = PasteNewSession,
			SharedExecutionLane = NewSessionInputExecutionLane,
			Title = "Paste Into New Session Prompt",
			RunsIn = CommandLocation.Web,
			Owner = CommandOwner.Client,
			Category = "Session",
			Description = "Paste the local clipboard into the focused new-session composer, including images.",
			Aliases = ["paste", "paste clipboard", "paste image", "paste into new session"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+v", When = "newSessionPromptFocused && !browserShell" }],
			KeybindingsActiveInModal = true,
			ShowInPalette = false,
		});

		registry.Register(new CommandDefinition {
			Id = ResuggestBranch,
			SharedExecutionLane = NewSessionInputExecutionLane,
			Title = "Suggest Branch Name Again",
			RunsIn = CommandLocation.Web,
			Owner = CommandOwner.Client,
			Category = "Session",
			Description = "Re-run the new-session composer's branch suggestion against the prompt as it now reads, "
				+ "replacing the current suggested or typed name. The composer suggests once on its own; this is how "
				+ "the user asks for another.",
			Aliases = ["resuggest branch", "rename branch suggestion", "suggest branch again", "new branch name"],
			ShowInPalette = false,
		});

		registry.Register(new CommandDefinition {
			Id = SubmitNewSession,
			SharedExecutionLane = NewSessionInputExecutionLane,
			Title = "Start New Session",
			RunsIn = CommandLocation.Web,
			Owner = CommandOwner.Client,
			Category = "Session",
			Description = "Submit the new-session composer while its prompt is focused.",
			Aliases = ["submit new session", "start new session", "create session from prompt"],
			DefaultKeybindings = [new CommandKeybinding { Key = "Shift+Enter", When = "newSessionPromptFocused" }],
			KeybindingsActiveInModal = true,
			ShowInPalette = false,
		});

		registry.Register(new CommandDefinition {
			Id = OpenPr,
			Title = "Open Pull Request…",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Open one of the repository's open pull requests as a session checked out on its head "
				+ "branch, seeding the session's agent with the PR's context.",
			Aliases = ["open pr", "open pull request", "review pr", "check out pr", "open github pr", "pull request"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+r" }],
		});

		registry.Register(new CommandDefinition {
			Id = OpenCurrentPr,
			Title = "Open Current Pull Request",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Open the pull request associated with the active branch in the system browser.",
			Aliases = ["open current pr", "view pull request", "open branch pr"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+g", When = "pullRequestAvailable" }],
			When = "pullRequestAvailable",
		});

		registry.Register(new CommandDefinition {
			Id = ForkSession,
			SharedExecutionLane = LifecycleExecutionLane,
			Title = "Fork Session",
			RunsIn = CommandLocation.Core,
			Category = "Session",
			Description = "Fork the invoking session into a new worktree branched off its HEAD, carrying a handoff "
				+ "brief to the new session's agent. 'branch' is required. 'handoff' is the "
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
			// ctrl+Tab cycles sessions, unguarded: the editor's editorFocused next-tab binding is the narrower
			// claim on the chord, so it takes the key while the editor holds focus and hands it back here when
			// it has no tab to step to. Literal ctrl (not $mod): Cmd+Tab is the OS app switcher.
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+Tab" }],
		});

		registry.Register(new CommandDefinition {
			Id = PrevSession,
			Title = "Previous Session",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Switch to the previous session on the rail (wraps around).",
			Aliases = ["previous session", "prev session", "switch to previous session"],
			// Mirror of NextSession: ctrl+Shift+Tab cycles backward, behind the editor's prev-tab binding.
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+Shift+Tab" }],
		});

		registry.Register(new CommandDefinition {
			Id = SwitchSession,
			Title = "Switch Session…",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Open the omnibar to pick a session to switch to.",
			Aliases = ["switch session", "go to session", "change session", "pick session"],
		});

		registry.Register(new CommandDefinition {
			Id = FocusSession,
			Title = "Focus Session",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Bring a specific session to the foreground by 'id' (its rail slot id), optionally "
				+ "naming the 'backendId' it lives on for a session on a connected remote backend. The "
				+ "optional 'incarnation' rejects a stale activation after a slot is reused. The "
				+ "programmatic counterpart of Switch Session… — notification click-through uses it; humans "
				+ "pick from the omnibar instead.",
			Aliases = ["focus session", "bring session to front", "go to session by id"],
			// Target-specific (which session) with no meaningful no-arg palette row; the human entry is the omnibar.
			ShowInPalette = false,
			ArgsSchemaJson = "{\"id\":{\"type\":\"string\",\"description\":\"Session id (rail slot id) to focus\"},"
				+ "\"backendId\":{\"type\":\"string\",\"description\":\"Backend the session lives on; omit for the page-serving backend\"},"
				+ "\"incarnation\":{\"type\":\"string\",\"description\":\"Exact live session incarnation; stale values are declined\"}}",
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
			SharedExecutionLane = LifecycleExecutionLane,
			Scope = CommandScope.Host,
			Title = "Load Session",
			RunsIn = CommandLocation.Core,
			Category = "Session",
			Description = "Load a dormant session's backend (agent / terminals / LSP) in the background, by 'id', "
				+ "WITHOUT switching the page to it — so its agent runs and reports status while you stay where you "
				+ "are. Use Switch Session to bring it to the foreground instead.",
			Aliases = ["load session", "start session", "wake session", "resume session in background"],
			// id-targeted (a specific dormant chip); loading the selected session is meaningless, so not in the palette.
			ShowInPalette = false,
			ArgsSchemaJson = "{\"id\":{\"type\":\"string\",\"description\":\"Session id to load in the background\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = UnloadSession,
			SharedExecutionLane = LifecycleExecutionLane,
			Scope = CommandScope.Host,
			Title = "Unload Session",
			RunsIn = CommandLocation.Core,
			Category = "Session",
			Description = "Unload the invoking session, or the session named by 'id', into a dormant chip: tear its live "
				+ "backend (agent / terminals / LSP) down but keep its worktree on disk so it can be reloaded later. "
				+ "Dormant chips sort to the bottom of the rail and are skipped when cycling. To remove the worktree "
				+ "entirely, use Delete Session.",
			Aliases = ["unload session", "park session", "make session dormant", "suspend session"],
			ArgsSchemaJson = "{\"id\":{\"type\":\"string\",\"description\":\"Session id to unload; omit for the invoking session\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = DeleteSession,
			SharedExecutionLane = LifecycleExecutionLane,
			Scope = CommandScope.Host,
			Title = "Delete Session",
			RunsIn = CommandLocation.Core,
			Category = "Session",
			Description = "Delete the invoking session, or the session named by 'id'. A Weavie-owned git worktree is "
				+ "removed but its branch is kept; a user-owned checkout is never removed. Refuses to remove a managed "
				+ "worktree with uncommitted changes unless 'force' is true. With "
				+ "'classify' true it deletes nothing and instead returns the worktree's state (clean/untracked/modified) "
				+ "for a confirm prompt. This is the programmatic entry (for agents); the interactive UI uses "
				+ "'Delete Session…' (weavie.session.deletePrompt).",
			Aliases = ["delete session", "remove session", "delete worktree", "remove worktree", "discard session"],
			// The human-facing entry is the guarded prompt (DeleteSessionPrompt); the raw delete stays reachable by Claude.
			ShowInPalette = false,
			ArgsSchemaJson = "{\"id\":{\"type\":\"string\",\"description\":\"Session id to delete; omit for the invoking session\"},"
				+ "\"force\":{\"type\":\"boolean\",\"description\":\"Delete even if the worktree has uncommitted changes\"},"
				+ "\"classify\":{\"type\":\"boolean\",\"description\":\"Don't delete; return the worktree state {state,label} for a confirm prompt\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = DeleteSessionPrompt,
			Title = "Delete Session…",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Open the delete confirmation for a session ('id', or the selected session): it classifies the "
				+ "worktree and escalates the confirm when there are untracked files or uncommitted changes. The "
				+ "interactive counterpart of weavie.session.delete.",
			Aliases = ["delete session", "remove session", "delete worktree"],
			ArgsSchemaJson = "{\"id\":{\"type\":\"string\",\"description\":\"Session id to delete; omit for the selected session\"}}",
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
			dispatcher.RegisterHandler(NewSession, (argsJson, ct) => NewSessionAsync(host, argsJson, ct)),
			dispatcher.RegisterHandler(ForkSession, (argsJson, ct) => host.ForkSessionAsync(
				new ForkSessionRequest {
					Branch = GetString(argsJson, "branch"),
					Handoff = GetString(argsJson, "handoff"),
				},
				ct)),
			dispatcher.RegisterHandler(LoadSession, (argsJson, ct) => host.LoadSessionAsync(GetString(argsJson, "id"), ct)),
			dispatcher.RegisterContextualHandler(
				UnloadSession,
				(argsJson, context, ct) => host.UnloadSessionAsync(GetString(argsJson, "id"), context, ct)),
			dispatcher.RegisterContextualHandler(DeleteSession, (argsJson, context, ct) => GetBool(argsJson, "classify")
				? host.ClassifyDeleteAsync(GetString(argsJson, "id"), ct)
				: host.DeleteSessionAsync(
					GetString(argsJson, "id"),
					GetBool(argsJson, "force"),
					context,
					ct)),
		};

		return new CompositeDisposable(registrations);
	}

	private static Task<CommandResult> NewSessionAsync(ISessionHost host, string? argsJson, CancellationToken ct) {
		NewSessionRequest request;
		try {
			request = ParseNewSessionRequest(argsJson);
		} catch (JsonException ex) {
			return Task.FromResult(CommandResult.Failure($"Invalid new session arguments: {ex.Message}"));
		}
		return host.NewSessionAsync(request, ct);
	}

	private static NewSessionRequest ParseNewSessionRequest(string? argsJson) {
		if (string.IsNullOrWhiteSpace(argsJson)) {
			return new NewSessionRequest();
		}

		using var doc = JsonDocument.Parse(argsJson);
		var args = doc.RootElement;
		if (args.ValueKind != JsonValueKind.Object) {
			throw new JsonException("Arguments must be a JSON object.");
		}

		return new NewSessionRequest {
			Branch = GetString(args, "branch"),
			Base = GetString(args, "base"),
			Prompt = GetString(args, "prompt"),
			Attachments = GetAttachments(args),
			AgentProviderId = GetString(args, "agentProviderId"),
			Existing = GetBool(args, "existing"),
		};
	}

	private static string? GetString(JsonElement args, string name) {
		if (!args.TryGetProperty(name, out var prop)) {
			return null;
		}

		return prop.ValueKind switch {
			JsonValueKind.String => prop.GetString(),
			JsonValueKind.Null => null,
			_ => prop.GetRawText(),
		};
	}

	private static bool GetBool(JsonElement args, string name) {
		if (!args.TryGetProperty(name, out var prop)) {
			return false;
		}

		return prop.ValueKind switch {
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.String => bool.TryParse(prop.GetString(), out bool b) ? b : prop.GetString() == "1",
			JsonValueKind.Number => prop.TryGetInt64(out long n) && n != 0,
			_ => false,
		};
	}

	private static IReadOnlyList<NewSessionAttachment> GetAttachments(JsonElement args) {
		if (!args.TryGetProperty("attachments", out var attachments)) {
			return [];
		}
		if (attachments.ValueKind != JsonValueKind.Array) {
			throw new JsonException("'attachments' must be an array.");
		}

		var parsed = new List<NewSessionAttachment>();
		foreach (var item in attachments.EnumerateArray()) {
			if (item.ValueKind != JsonValueKind.Object) {
				throw new JsonException("Every attachment must be an object.");
			}
			parsed.Add(new NewSessionAttachment {
				Id = AttachmentString(item, "id"),
				Mime = AttachmentString(item, "mime"),
				DataB64 = AttachmentString(item, "dataB64"),
			});
		}
		return parsed;
	}

	private static string AttachmentString(JsonElement attachment, string name) {
		if (!attachment.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) {
			throw new JsonException($"Attachment '{name}' must be a string.");
		}
		return property.GetString()!;
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
