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
				address = new {
					slot = session.Address.Slot,
					incarnation = session.Address.Incarnation,
				},
			}));
	}

	private async Task<CommandResult> CloseShellTerminalAsync(
		HostSession session,
		string? argsJson,
		CancellationToken ct) {
		var (Result, Terminal) = await _ui.InvokeAsync(
			() => Task.FromResult(DetachShellTerminal(session, argsJson)),
			ct).ConfigureAwait(false);
		if (Terminal is not null) {
			await Task.Run(Terminal.DisposePermanently).ConfigureAwait(false);
		}
		return Result;
	}

	private static (CommandResult Result, ShellTerminal? Terminal) DetachShellTerminal(
		HostSession session,
		string? argsJson) {
		if (!TryTerminalArgs(argsJson, out string? id, out bool force, out string? error)) {
			return (CommandResult.Failure(error!), null);
		}
		var terminal = string.IsNullOrEmpty(id) ? session.Shells.Primary : session.Shells.Find(id);
		if (terminal is null) {
			return (CommandResult.Failure(id is null
				? "No shell terminal is open."
				: $"No shell terminal has id '{id}'."), null);
		}
		return session.Shells.DetachForClose(terminal.Id, force, out var detached) switch {
			ShellTerminalCloseResult.Closed => (CommandResult.Success("Closed the terminal."), detached),
			ShellTerminalCloseResult.Busy => (CommandResult.Failure(
				"The terminal is running a foreground job.",
				"{\"busy\":true}"), null),
			_ => (CommandResult.Failure($"No shell terminal has id '{terminal.Id}'."), null),
		};
	}

	private static CommandResult ReopenShellTerminal(HostSession session, string? argsJson) {
		if (!TryTerminalArgs(argsJson, out string? id, out _, out string? error)) {
			return CommandResult.Failure(error!);
		}
		var terminal = string.IsNullOrEmpty(id) ? session.Shells.Primary : session.Shells.Find(id);
		if (terminal is null) {
			return CommandResult.Failure(id is null
				? "No shell terminal is open."
				: $"No shell terminal has id '{id}'.");
		}
		terminal.Controller.Restart();
		return CommandResult.Success("Reopened the terminal.");
	}

	private static bool TryTerminalArgs(
		string? argsJson,
		out string? id,
		out bool force,
		out string? error) {
		id = null;
		force = false;
		error = null;
		if (string.IsNullOrWhiteSpace(argsJson)) {
			return true;
		}
		try {
			using var document = JsonDocument.Parse(argsJson);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) {
				error = "Terminal command arguments must be an object.";
				return false;
			}
			if (root.TryGetProperty("id", out var idElement)) {
				if (idElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idElement.GetString())) {
					error = "Terminal id must be a non-empty string.";
					return false;
				}
				id = idElement.GetString();
			}
			if (root.TryGetProperty("force", out var forceElement)) {
				if (forceElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
					error = "Terminal force must be a boolean.";
					return false;
				}
				force = forceElement.GetBoolean();
			}
			return true;
		} catch (JsonException ex) {
			error = $"Invalid terminal command arguments: {ex.Message}";
			return false;
		}
	}
}
