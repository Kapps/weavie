using System.Text.Json;
using Weavie.Core.Commands;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private void RegisterShellTerminalHandlers(HostSession session) {
		session.Shells.Changed += terminals => {
			if (SlotFor(session) is { } slot && ReferenceEquals(slot.Session, session)) {
				slot.ShellTerminals = terminals;
				PersistSessionState();
			}
		};
		session.Commands.RegisterHandler(
			CoreCommands.NewTerminal,
			(_, ct) => _ui.InvokeAsync(
				() => Task.FromResult(CreateShellTerminal(session)),
				ct));
		session.Commands.RegisterHandler(
			CoreCommands.CloseTerminal,
			(argsJson, ct) => CloseShellTerminalAsync(session, argsJson, ct));
		session.Commands.RegisterHandler(
			CoreCommands.ReopenTerminal,
			(argsJson, _) => Task.FromResult(ReopenShellTerminal(session, argsJson)));
	}

	private static CommandResult CreateShellTerminal(HostSession session) {
		var terminal = session.Shells.Create();
		return CommandResult.Success(
			null,
			JsonSerializer.Serialize(new {
				activateTerminal = true,
				terminalId = terminal.Id,
				address = LiveAddress(session),
			}));
	}

	private async Task<CommandResult> CloseShellTerminalAsync(
		HostSession session,
		string? argsJson,
		CancellationToken ct) {
		var (result, terminal) = await _ui.InvokeAsync(
			() => Task.FromResult(DetachShellTerminal(session, argsJson)),
			ct).ConfigureAwait(false);
		if (terminal is not null) {
			await Task.Run(terminal.DisposePermanently).ConfigureAwait(false);
		}
		return result;
	}

	private static (CommandResult Result, ShellTerminal? Terminal) DetachShellTerminal(
		HostSession session,
		string? argsJson) {
		var args = ParseTerminalArgs(argsJson);
		if (args.Error is not null) {
			return (CommandResult.Failure(args.Error), null);
		}
		return session.Shells.DetachForClose(args.Id, args.Force, out var detached) switch {
			ShellTerminalCloseResult.Closed => (CommandResult.Success("Closed the terminal."), detached),
			ShellTerminalCloseResult.Busy => (CommandResult.Failure(
				"The terminal is running a foreground job.",
				"{\"busy\":true}"), null),
			_ => (CommandResult.Failure(MissingTerminal(args.Id)), null),
		};
	}

	private static CommandResult ReopenShellTerminal(HostSession session, string? argsJson) {
		var args = ParseTerminalArgs(argsJson);
		if (args.Error is not null) {
			return CommandResult.Failure(args.Error);
		}
		var terminal = session.Shells.Resolve(args.Id);
		if (terminal is null) {
			return CommandResult.Failure(MissingTerminal(args.Id));
		}
		terminal.Controller.Restart();
		return CommandResult.Success("Reopened the terminal.");
	}

	private static TerminalArgs ParseTerminalArgs(string? argsJson) {
		if (string.IsNullOrWhiteSpace(argsJson)) {
			return new TerminalArgs(null, false, null);
		}
		try {
			using var document = JsonDocument.Parse(argsJson);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) {
				return new TerminalArgs(null, false, "Terminal command arguments must be an object.");
			}
			string? id = null;
			if (root.TryGetProperty("id", out var idElement)) {
				if (idElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idElement.GetString())) {
					return new TerminalArgs(null, false, "Terminal id must be a non-empty string.");
				}
				id = idElement.GetString();
			}
			bool force = false;
			if (root.TryGetProperty("force", out var forceElement)) {
				if (forceElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
					return new TerminalArgs(null, false, "Terminal force must be a boolean.");
				}
				force = forceElement.GetBoolean();
			}
			return new TerminalArgs(id, force, null);
		} catch (JsonException ex) {
			return new TerminalArgs(null, false, $"Invalid terminal command arguments: {ex.Message}");
		}
	}

	private static string MissingTerminal(string? id) => id is null
		? "No shell terminal is open."
		: $"No shell terminal has id '{id}'.";

	private sealed record TerminalArgs(string? Id, bool Force, string? Error);
}
