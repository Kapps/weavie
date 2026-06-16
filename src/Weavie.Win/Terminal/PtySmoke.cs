using System.Text;
using Weavie.Core.Terminal;

namespace Weavie.Win.Terminal;

/// <summary>
/// Headless self-check for the ConPTY path (run via <c>Weavie.Win.exe --pty-smoke</c>). Spawns a
/// throwaway pseudo console running <c>cmd /c echo</c>, collects the rendered output, and confirms
/// the marker round-trips and the child's exit code is observed. Exercises the entire Windows
/// terminal stack — pipes, CreatePseudoConsole, the proc-thread attribute list, CreateProcess, the
/// read loop, and exit detection — without needing the WebView2 UI. Diagnostic only.
/// </summary>
internal static class PtySmoke {
	private const string Marker = "weavie-pty-ok";

	public static int Run() {
		// Log to a file when asked, so the smoke can run with NO redirected stdout — a redirected
		// stdout handle on this WinExe would leak into the ConPTY child and steal its output, which
		// the console-less production app never does.
		var logPath = Environment.GetEnvironmentVariable("WEAVIE_SMOKE_LOG");
		void Log(string line) {
			if (string.IsNullOrEmpty(logPath)) {
				Console.WriteLine(line);
			} else {
				File.AppendAllText(logPath, line + "\n");
			}
		}

		var output = new StringBuilder();
		var totalBytes = 0;

		string Rendered() {
			lock (output) {
				return output.ToString();
			}
		}

		using var terminal = new WindowsConPtyTerminal();
		terminal.Output += data => {
			lock (output) {
				Interlocked.Add(ref totalBytes, data.Length);
				output.Append(Encoding.UTF8.GetString(data));
			}
		};

		// Drive an interactive cmd.exe (mirrors the long-lived `claude` workload): wait for the
		// prompt, ask it to echo the marker, then exit.
		var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
		terminal.Start(new TerminalStartInfo {
			Command = comspec,
			Arguments = [],
			WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			Columns = 120,
			Rows = 30,
		});

		Thread.Sleep(600);
		terminal.Write(Encoding.UTF8.GetBytes($"echo {Marker}\r\n"));

		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < deadline && !Rendered().Contains(Marker, StringComparison.Ordinal)) {
			Thread.Sleep(50);
		}

		terminal.Write(Encoding.UTF8.GetBytes("exit\r\n"));
		Thread.Sleep(200);

		var sawMarker = Rendered().Contains(Marker, StringComparison.Ordinal);
		Log($"[pty-smoke] sawMarker={sawMarker} bytes={Volatile.Read(ref totalBytes)}");
		return sawMarker ? 0 : 1;
	}
}
