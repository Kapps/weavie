using System.Text;
using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.TestRunning;

namespace Weavie.Hosting;

// The weavie.tests.* Core handlers: read the workspace test profile, pick the file's rule, compose a shell
// command, and write it into the session's shell pane (visible, Claude-observable). See
// docs/specs/test-running-and-workspace-setup.md.
public sealed partial class HostCore {
	// Registers the run handlers on a session's dispatcher (called from WireSession) so an MCP invocation from a
	// worktree session's Claude runs in that session's own shell.
	private void RegisterTestRunHandlers(HostSession session) {
		session.Commands.RegisterHandler(CoreCommands.RunTests, (argsJson, _) => {
			var (file, name) = ParseRunArgs(argsJson);
			return Task.FromResult(RunTests(session, file, name));
		});
		session.Commands.RegisterHandler(CoreCommands.RunTestsInFile, (argsJson, _) => {
			var (file, _) = ParseRunArgs(argsJson);
			return Task.FromResult(RunTests(session, file, testName: null));
		});
	}

	private CommandResult RunTests(HostSession session, string? fileArg, string? testName) {
		string? file = string.IsNullOrEmpty(fileArg) ? session.Editor.Active?.FilePath : fileArg;
		if (string.IsNullOrEmpty(file)) {
			return CommandResult.Failure("No file to run tests for — open a test file or pass a file path.");
		}

		// The profile is a per-workspace setting, resolved against this HostCore's workspace (shared by all its
		// sessions/worktrees); the file path and shell are the session's own.
		string profileJson = _settings.Resolve(TestSettings.Profile, WorkspaceRoot).Value as string ?? string.Empty;
		if (string.IsNullOrWhiteSpace(profileJson)) {
			return CommandResult.Failure("No test profile is configured — run 'Set Up This Workspace' first.");
		}

		if (!TestProfile.TryParse(profileJson, out var profile, out string parseError)) {
			return CommandResult.Failure($"The test profile is invalid: {parseError}");
		}

		string relative = Path.GetRelativePath(session.WorkspaceRoot, file);
		var rule = TestRuleMatcher.Match(profile, relative);
		if (rule is null) {
			return CommandResult.Failure($"No test rule in the profile matches {relative}.");
		}

		if (session.Shell.HasForegroundJob) {
			Notify("error", "Tests not started: the shell is busy running a job.", "tests-shell-busy");
			return CommandResult.Failure("The shell is busy running a job; wait for it to finish and retry.");
		}

		var kind = testName is null ? TestCommandKind.RunFile : TestCommandKind.RunOne;
		if (!TestCommandComposer.TryCompose(
			rule, kind, Path.GetFullPath(file), testName, ShellQuotingForShell(), out string command, out string composeError)) {
			return CommandResult.Failure(composeError);
		}

		session.Shell.Write(Encoding.UTF8.GetBytes(command + "\r"));
		// Reveal the run only when this session owns the foreground; a background session's shell isn't visible.
		if (IsActiveSession(session)) {
			_bridge.PostToWeb("{\"type\":\"focus-pane\",\"kind\":\"terminal:shell\"}");
		}

		return CommandResult.Success($"Running: {command}");
	}

	private static (string? File, string? Name) ParseRunArgs(string? argsJson) {
		if (string.IsNullOrWhiteSpace(argsJson)) {
			return (null, null);
		}

		try {
			using var doc = JsonDocument.Parse(argsJson);
			var root = doc.RootElement;
			string? file = root.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
			string? name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
			return (file, name);
		} catch (JsonException) {
			return (null, null);
		}
	}

	// POSIX single-quoting for everything except PowerShell / cmd.exe, which get the '' -doubling treatment.
	private ShellQuoting ShellQuotingForShell() {
		string shell = _settings.GetString("terminal.shell") ?? string.Empty;
		string name = Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();
		bool powerShellFamily = name is "pwsh" or "powershell" || Path.GetFileName(shell).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
		return powerShellFamily ? ShellQuoting.PowerShell : ShellQuoting.Posix;
	}
}
