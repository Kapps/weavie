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
		session.Commands.RegisterHandler(CoreCommands.RunTests, (argsJson, _) =>
			Task.FromResult(RunFromArgs(session, argsJson, includeName: true)));
		session.Commands.RegisterHandler(CoreCommands.RunTestsInFile, (argsJson, _) =>
			Task.FromResult(RunFromArgs(session, argsJson, includeName: false)));
	}

	private CommandResult RunFromArgs(HostSession session, string? argsJson, bool includeName) {
		if (!TryParseRunArgs(argsJson, out string? file, out string? name, out string? error)) {
			return CommandResult.Failure(error!);
		}

		return RunTests(session, file, includeName ? name : null);
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
			Notify("warn", "Tests not started: the shell is busy running a job.", "tests-shell-busy");
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

	// Parses {file?, name?}. Absent/empty args is valid (file falls back to the active editor); malformed JSON is
	// a loud failure, never a silent fall-through that could run a different test than asked.
	private static bool TryParseRunArgs(string? argsJson, out string? file, out string? name, out string? error) {
		file = null;
		name = null;
		error = null;
		if (string.IsNullOrWhiteSpace(argsJson)) {
			return true;
		}

		try {
			using var doc = JsonDocument.Parse(argsJson);
			var root = doc.RootElement;
			file = root.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
			name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
			return true;
		} catch (JsonException ex) {
			error = $"Could not run tests: invalid command arguments ({ex.Message}).";
			return false;
		}
	}

	/// <summary>The page-bootstrap fragment seeding <c>window.__WEAVIE_TEST_PROFILE__</c> with this workspace's raw test.profile (empty when unset).</summary>
	public string BuildTestProfileScript() =>
		"window.__WEAVIE_TEST_PROFILE__ = " + JsonSerializer.Serialize(ResolvedTestProfile()) + ";";

	// Re-push the workspace's test profile so the page's lens provider refreshes (fired on a test.profile change).
	private void PushTestProfileToWeb() =>
		_bridge.PostToWeb("{\"type\":\"test-profile\",\"profile\":" + JsonSerializer.Serialize(ResolvedTestProfile()) + "}");

	private string ResolvedTestProfile() => _settings.Resolve(TestSettings.Profile, WorkspaceRoot).Value as string ?? string.Empty;

	// PowerShell (the Windows default) gets '' -doubling single-quotes; everything else POSIX single-quoting.
	// cmd.exe isn't specially handled — it's never the default, and single-quoting isn't its convention.
	private ShellQuoting ShellQuotingForShell() {
		string shell = _settings.GetString("terminal.shell") ?? string.Empty;
		string name = Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();
		return name is "pwsh" or "powershell" ? ShellQuoting.PowerShell : ShellQuoting.Posix;
	}
}
