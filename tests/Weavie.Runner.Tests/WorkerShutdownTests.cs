using System.Collections.Concurrent;
using System.Diagnostics;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Runner.Tests;

/// <summary>Stopping the real worker runs its orderly shutdown, which removes the pipes it owns.</summary>
public sealed class WorkerShutdownTests {
	[Fact]
	public async Task DisposingTheManager_ShutsTheWorkerDownWithoutStrandingItsHookPipe() {
		// Named pipes are Unix domain socket files only off Windows.
		if (OperatingSystem.IsWindows()) return;
		using var root = new TempDirectory("wv-worker");
		string worker = IsolatedWorker(root);
		var logs = new ConcurrentQueue<SupervisorLogEntry>();
		var manager = new BackendManager(
			new RunnerOptions { WorkspaceRoot = root.CreateDirectory("ws"), HeadlessPath = "headless", RunnerToken = "runner" },
			new HeadlessLauncher(() => worker, "127.0.0.1", logs.Enqueue),
			"127.0.0.1");
		try {
			var backend = manager.Ensure();
			var ready = Stopwatch.StartNew();
			while (await manager.StatusAsync(backend) != "running") {
				Assert.True(ready.Elapsed < TimeSpan.FromSeconds(60), "the worker never became ready");
				await Task.Delay(100);
			}
			await Wait.UntilAsync(() => HookPipes().Length > 0, TimeSpan.FromSeconds(30));
		} finally {
			await manager.DisposeAsync();
		}

		Assert.Empty(HookPipes());
		Assert.DoesNotContain(logs, entry => entry.Message.Contains("force-stopped", StringComparison.Ordinal));
	}

	private static string[] HookPipes() => Directory.GetFileSystemEntries(Path.GetTempPath(), "CoreFxPipe_weavie-hook-*");

	// The worker inherits this process's environment; its home and Claude config stay inside the test's directory.
	[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
	private static string IsolatedWorker(TempDirectory root) {
		string worker = Path.GetFullPath(Path.Combine(
			AppContext.BaseDirectory, "../../../../..", "src", "Weavie.Headless", "bin",
			new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "Weavie.Headless.dll"));
		string home = root.CreateDirectory("home");
		string script = root.WriteFile("worker.sh", $"""
			#!/bin/sh
			export HOME='{home}'
			export CLAUDE_CONFIG_DIR='{home}/.claude'
			exec dotnet '{worker}' "$@"
			""");
		File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		return script;
	}
}
