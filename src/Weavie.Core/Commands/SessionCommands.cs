using System.Globalization;
using System.Text.Json;
using Weavie.Core.Sessions;

namespace Weavie.Core.Commands;

/// <summary>
/// Declares the multi-session commands and wires the Core-handled ones (new / fork / close) to a host's
/// <see cref="ISessionHost"/>. The switch commands (next / prev / switch) run in the web (the rail). Like
/// <c>ThemeCommands</c>, the declarations live in Core so every trigger sees them; the host registers the
/// handlers once it has a session host. See <c>docs/specs/multi-session-and-worktrees.md</c>.
/// </summary>
public static class SessionCommands {
	/// <summary>Creates a new session on its own worktree + branch (args <c>branch</c>/<c>base</c>/<c>prompt</c>); the programmatic entry (Claude). The interactive UI uses <see cref="NewSessionPrompt"/>.</summary>
	public const string NewSession = "weavie.session.new";

	/// <summary>Opens the interactive new-session prompt (branch name + base) in the UI; <c>$mod+Shift+n</c>.</summary>
	public const string NewSessionPrompt = "weavie.session.newPrompt";

	/// <summary>Forks the current session into a new worktree off its HEAD (args <c>branch</c>/<c>handoff</c>).</summary>
	public const string ForkSession = "weavie.session.fork";

	/// <summary>Switches to the next session on the rail; <c>$mod+Shift+]</c>.</summary>
	public const string NextSession = "weavie.session.next";

	/// <summary>Switches to the previous session on the rail; <c>$mod+Shift+[</c>.</summary>
	public const string PrevSession = "weavie.session.prev";

	/// <summary>Opens the omnibar to pick a session to switch to.</summary>
	public const string SwitchSession = "weavie.session.switch";

	/// <summary>Switches to the Nth session on the rail (1-based); bound to <c>$mod+Shift+1..9</c>, dispatched with <c>{ "index": N }</c>.</summary>
	public const string SelectSessionByIndex = "weavie.session.selectByIndex";

	/// <summary>Closes a session (the active one, or the <c>id</c> arg), keeping its worktree on disk.</summary>
	public const string CloseSession = "weavie.session.close";

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
				+ "default) or 'main'. An optional 'prompt' is sent to the new session's Claude as its first message. "
				+ "This is the programmatic entry (for Claude); the interactive UI uses 'New Session…' (weavie.session.newPrompt).",
			Aliases = ["new session", "create session", "new worktree", "branch session", "new agent", "another claude", "spin up a session"],
			// No default keybinding + hidden from the palette: the human-facing entry is the interactive prompt
			// (NewSessionPrompt, bound to $mod+Shift+n). Still reachable by Claude via listCommands/runCommand.
			ShowInPalette = false,
			ArgsSchemaJson = "{\"branch\":{\"type\":\"string\"},\"base\":{\"type\":\"string\",\"enum\":[\"current\",\"main\"]},\"prompt\":{\"type\":\"string\"}}",
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
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+]" }],
		});

		registry.Register(new CommandDefinition {
			Id = PrevSession,
			Title = "Previous Session",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Switch to the previous session on the rail (wraps around).",
			Aliases = ["previous session", "prev session", "switch to previous session"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+[" }],
		});

		registry.Register(new CommandDefinition {
			Id = SwitchSession,
			Title = "Switch Session…",
			RunsIn = CommandLocation.Web,
			Category = "Session",
			Description = "Open the omnibar to pick a session to switch to.",
			Aliases = ["switch session", "go to session", "change session", "pick session"],
		});

		// $mod+Shift+1..9 → switch to the Nth session on the rail (the session analogue of the pane-focus
		// Ctrl+1..9). Keybinding-only + hidden from the palette: "switch to session 3" is no clearer a palette
		// row than the pane equivalent, and the human-facing picker is Switch Session… Each default binding
		// carries its own 1-based index argument; the web rail switches to that session if one exists there.
		var indexBindings = new List<CommandKeybinding>(9);
		for (int i = 1; i <= 9; i++) {
			string n = i.ToString(CultureInfo.InvariantCulture);
			indexBindings.Add(new CommandKeybinding { Key = $"$mod+Shift+{n}", ArgsJson = $"{{\"index\":{n}}}" });
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
			Id = CloseSession,
			Title = "Close Session",
			RunsIn = CommandLocation.Core,
			Category = "Session",
			Description = "Close a session (the active one, or the session 'id'). The worktree is kept on disk and "
				+ "can be reopened; discarding it (and its branch) is a separate, guarded worktree action.",
			Aliases = ["close session", "end session", "close this session"],
			ArgsSchemaJson = "{\"id\":{\"type\":\"string\",\"description\":\"Session id to close; omit for the active session\"}}",
		});
	}

	/// <summary>
	/// Registers the Core-handled session commands (new / fork / close) onto <paramref name="dispatcher"/>,
	/// routing each to <paramref name="host"/> with leniently-parsed arguments. Returns a disposable that
	/// unregisters them all.
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
				},
				ct)),
			dispatcher.RegisterHandler(ForkSession, (argsJson, ct) => host.ForkSessionAsync(
				new ForkSessionRequest {
					Branch = GetString(argsJson, "branch"),
					Handoff = GetString(argsJson, "handoff"),
				},
				ct)),
			dispatcher.RegisterHandler(CloseSession, (argsJson, ct) => host.CloseSessionAsync(GetString(argsJson, "id"), ct)),
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
