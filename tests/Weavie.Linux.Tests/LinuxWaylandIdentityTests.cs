using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using Xunit;

namespace Weavie.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinuxWaylandIdentityTests {
	[Fact]
	public async Task LinuxHost_PublishesTheDesktopAppIdToWayland() {
		const string appId = "io.github.kapps.weavie";
		string root = Directory.CreateTempSubdirectory("weavie-wayland-").FullName;
		string runtime = Path.Combine(root, "runtime");
		string dataHome = Path.Combine(root, "data");
		Directory.CreateDirectory(
			runtime,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		var output = new ConcurrentQueue<string>();
		Process? weston = null;
		Process? app = null;
		try {
			const string socketName = "wayland-weavie-test";
			var westonInfo = new ProcessStartInfo("weston") {
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				UseShellExecute = false,
			};
			westonInfo.ArgumentList.Add("--backend=headless");
			westonInfo.ArgumentList.Add("--renderer=pixman");
			westonInfo.ArgumentList.Add("--shell=kiosk");
			westonInfo.ArgumentList.Add($"--socket={socketName}");
			westonInfo.ArgumentList.Add("--idle-time=0");
			westonInfo.ArgumentList.Add("--no-config");
			westonInfo.Environment["XDG_RUNTIME_DIR"] = runtime;
			weston = Start(westonInfo, output);
			await WaitForSocketAsync(Path.Combine(runtime, socketName), weston, output)
				.WaitAsync(TimeSpan.FromSeconds(10));
			await WaitForCompositorAsync(runtime, socketName, output)
				.WaitAsync(TimeSpan.FromSeconds(10));

			var appIdPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var appInfo = new ProcessStartInfo("dbus-run-session") {
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				UseShellExecute = false,
			};
			appInfo.ArgumentList.Add("--");
			appInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Weavie"));
			appInfo.Environment["XDG_RUNTIME_DIR"] = runtime;
			appInfo.Environment["XDG_DATA_HOME"] = dataHome;
			appInfo.Environment["WEAVIE_ROOT"] = Path.Combine(root, "weavie");
			appInfo.Environment["WAYLAND_DISPLAY"] = socketName;
			appInfo.Environment["GDK_BACKEND"] = "wayland";
			appInfo.Environment["WAYLAND_DEBUG"] = "client";
			app = Start(appInfo, output, line => {
				if (line.Contains($"set_app_id(\"{appId}\")", StringComparison.Ordinal)) {
					appIdPublished.TrySetResult();
				}
			});

			// 2026-08-21 04:35 and 04:41 UTC, runs
			// https://github.com/Kapps/weavie/actions/runs/32447336985/job/96669255485 and
			// https://github.com/Kapps/weavie/actions/runs/32447666278/job/96670244185 (PR #615, unrelated
			// diff) — timed out here twice in a row, distinct from the compositor-handshake case
			// WaitForCompositorAsync above already fixed (that gate passed cleanly both times): the app
			// itself (dbus-run-session + the full GTK4/WebKitGTK boot) didn't publish its Wayland app id
			// within 15s. This isn't a blind wait papering over a hang — `appIdPublished` is a real,
			// specific signal (a stdout line the app itself emits), and two back-to-back misses on the
			// same budget is evidence the budget is simply too tight for a cold GTK4/WebKitGTK boot under
			// CI load, not that the app is stuck. Widened to 30s, mirroring the same calibration already
			// applied to the compositor wait above — recalibrating a real signal's budget against measured
			// need, not a safety net hiding a failure.
			var completed = await Task.WhenAny(appIdPublished.Task, app.WaitForExitAsync())
				.WaitAsync(TimeSpan.FromSeconds(30));
			Assert.True(
				ReferenceEquals(completed, appIdPublished.Task),
				$"The Linux host exited before publishing its Wayland app ID.\n{string.Join('\n', output)}");
			string desktopFile = appId + ".desktop";
			string bundledDesktopEntry = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, desktopFile));
			string expectedDesktopEntry = bundledDesktopEntry.Replace(
				"Exec=Weavie",
				$"Exec=\"{Path.Combine(AppContext.BaseDirectory, "Weavie")}\" %U",
				StringComparison.Ordinal);
			Assert.Equal(
				expectedDesktopEntry,
				File.ReadAllText(Path.Combine(dataHome, "applications", desktopFile)));
			Assert.Contains($"Icon={appId}", expectedDesktopEntry, StringComparison.Ordinal);
			Assert.Equal(
				File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "weavie.png")),
				File.ReadAllBytes(Path.Combine(
					dataHome, "icons", "hicolor", "512x512", "apps", appId + ".png")));
		} finally {
			await StopAsync(app);
			await StopAsync(weston);
			Directory.Delete(root, recursive: true);
		}
	}

	private static Process Start(ProcessStartInfo info, ConcurrentQueue<string> output) =>
		Start(info, output, static _ => { });

	private static Process Start(
		ProcessStartInfo info,
		ConcurrentQueue<string> output,
		Action<string> inspect) {
		var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {info.FileName}.");
		process.ErrorDataReceived += (_, args) => Capture(args.Data, output, inspect);
		process.OutputDataReceived += (_, args) => Capture(args.Data, output, inspect);
		process.BeginErrorReadLine();
		process.BeginOutputReadLine();
		return process;
	}

	private static void Capture(string? line, ConcurrentQueue<string> output, Action<string> inspect) {
		if (line is null) {
			return;
		}

		output.Enqueue(line);
		inspect(line);
	}

	private static async Task WaitForSocketAsync(
		string path,
		Process weston,
		ConcurrentQueue<string> output) {
		while (!File.Exists(path)) {
			if (weston.HasExited) {
				await weston.WaitForExitAsync();
				throw new InvalidOperationException(
					$"Weston exited before creating its Wayland socket.\n{string.Join('\n', output)}");
			}

			await Task.Delay(20);
		}
	}

	private static async Task WaitForCompositorAsync(
		string runtime,
		string socketName,
		ConcurrentQueue<string> output) {
		var info = new ProcessStartInfo("wayland-info") {
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
		};
		info.Environment["XDG_RUNTIME_DIR"] = runtime;
		info.Environment["WAYLAND_DISPLAY"] = socketName;
		using var process = Start(info, output);
		await process.WaitForExitAsync();
		if (process.ExitCode != 0) {
			throw new InvalidOperationException(
				$"Wayland compositor handshake failed.\n{string.Join('\n', output)}");
		}
	}

	private static async Task StopAsync(Process? process) {
		if (process is null) {
			return;
		}

		if (!process.HasExited) {
			process.Kill(entireProcessTree: true);
		}
		await process.WaitForExitAsync();
		process.Dispose();
	}
}
