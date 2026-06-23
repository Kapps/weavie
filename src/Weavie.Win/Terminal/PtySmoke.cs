using System.Text;
using Weavie.Core.Terminal;

namespace Weavie.Win.Terminal;

/// <summary>
/// Headless ConPTY self-check (<c>Weavie.Win.exe --pty-smoke</c>): spawns a throwaway pseudo console, echoes a
/// marker, and confirms it round-trips — exercising the Windows terminal stack without the UI. Diagnostic only.
/// </summary>
internal static class PtySmoke {
	private const string Marker = "weavie-pty-ok";

	public static int Run() {
		// Optionally log to WEAVIE_SMOKE_LOG, keeping the smoke's diagnostics out of a redirected stdout stream.
		string? logPath = Environment.GetEnvironmentVariable("WEAVIE_SMOKE_LOG");
		void Log(string line) {
			if (string.IsNullOrEmpty(logPath)) {
				Console.WriteLine(line);
			} else {
				File.AppendAllText(logPath, line + "\n");
			}
		}

		var output = new StringBuilder();
		int totalBytes = 0;

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

		// Drive an interactive cmd.exe (mirrors the long-lived `claude` workload): echo the marker, exit.
		string comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
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

		terminal.Write("exit\r\n"u8.ToArray());
		Thread.Sleep(200);

		bool sawMarker = Rendered().Contains(Marker, StringComparison.Ordinal);
		Log($"[pty-smoke] sawMarker={sawMarker} bytes={Volatile.Read(ref totalBytes)}");
		return sawMarker ? 0 : 1;
	}
}
