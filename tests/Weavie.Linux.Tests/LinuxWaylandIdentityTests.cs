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

			var completed = await Task.WhenAny(appIdPublished.Task, app.WaitForExitAsync())
				.WaitAsync(TimeSpan.FromSeconds(15));
			Assert.True(
				ReferenceEquals(completed, appIdPublished.Task),
				$"The Linux host exited before publishing its Wayland app ID.\n{string.Join('\n', output)}");
			string desktopFile = appId + ".desktop";
			string installedDesktopEntry = File.ReadAllText(Path.Combine(dataHome, "applications", desktopFile));
			Assert.Equal(
				File.ReadAllText(Path.Combine(AppContext.BaseDirectory, desktopFile)),
				installedDesktopEntry);
			Assert.Contains($"Icon={appId}", installedDesktopEntry, StringComparison.Ordinal);
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
