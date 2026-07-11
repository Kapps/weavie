using System.Text;
using System.Text.RegularExpressions;
using Weavie.Core.Mcp;
using Weavie.Core.Terminal;

// Dev harness (not shipped): launches the real interactive `claude` wired to our IDE-MCP server and verifies
// the handshake (connect/auth/initialize/tools/list) end to end. It does not puppeteer claude's TUI — the only
// input is Enter, retried until connected, to clear the first-run "trust this folder?" prompt. openDiff
// correctness is covered deterministically by McpServerTests. Spawns claude => spends subscription usage.
// Config via env:
//   WEAVIE_HARNESS_SECONDS   how long to run (default 25)
//   WEAVIE_HARNESS_WORKSPACE workspace dir (default a fresh /tmp dir)

int seconds = int.TryParse(Environment.GetEnvironmentVariable("WEAVIE_HARNESS_SECONDS"), out int s) ? s : 25;
string? workspace = Environment.GetEnvironmentVariable("WEAVIE_HARNESS_WORKSPACE");
if (string.IsNullOrEmpty(workspace)) {
	workspace = Path.Combine(Path.GetTempPath(), "weavie-ide-harness");
}

Directory.CreateDirectory(workspace);
Console.WriteLine($"[harness] workspace: {workspace}");

var flags = new HandshakeFlags();
var presenter = FakeDiffPresenter.AlwaysKeep();
var credential = AgentSessionCredential.Create();
await using var server = new McpServer(
	credential.Token, presenter, [workspace], "weavie", settings: null, registryMode: false,
	exposeIdeTools: true, layout: null, editor: null, commands: null, keybindings: null, themeOverrides: null,
	currentSessionId: null);
int port = server.Start();
IdeLockFile.Write(port, [workspace], "weavie", credential.Token);
using var lockCleanup = new LockCleanup(port);

server.Log += line => {
	Console.WriteLine($"[mcp] {line}");
	if (line.Contains("connected + authenticated", StringComparison.Ordinal)) {
		flags.Connected = true;
	}

	if (line.Contains("method=initialize", StringComparison.Ordinal)) {
		flags.Initialized = true;
	}

	if (line.Contains("method=tools/list", StringComparison.Ordinal)) {
		flags.ToolsListed = true;
	}
};

Console.WriteLine($"[harness] IDE-MCP server on 127.0.0.1:{port}");
Console.WriteLine($"[harness] lock file: {IdeLockFile.PathForPort(port)}");
Console.WriteLine($"[harness] env inject: CLAUDE_CODE_SSE_PORT={port} ENABLE_IDE_INTEGRATION=true");

const string claudeCmd = "exec claude";
string? shell = Environment.GetEnvironmentVariable("SHELL");
if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) {
	shell = "/bin/zsh";
}

string ptyLogPath = Path.Combine(Path.GetTempPath(), "weavie-harness-pty.log");
await using var ptyLog = new FileStream(ptyLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);

using var terminal = new PosixPtyTerminal();
var ansi = new Regex(@"\x1b\[[0-9;?]*[A-Za-z]|\x1b\][^\x07]*\x07|\x1b[()][AB0]", RegexOptions.Compiled);
terminal.Output += bytes => {
	ptyLog.Write(bytes, 0, bytes.Length);
	ptyLog.Flush();
};

var env = new Dictionary<string, string>(StringComparer.Ordinal) {
	["CLAUDE_CODE_SSE_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
	["ENABLE_IDE_INTEGRATION"] = "true",
};
terminal.Start(new TerminalStartInfo {
	Command = shell,
	Arguments = ["-l", "-c", claudeCmd],
	WorkingDirectory = workspace,
	Environment = env,
	RemoveEnvironment = ["ANTHROPIC_API_KEY"],
	Columns = 100,
	Rows = 40,
});
Console.WriteLine($"[harness] spawned: {shell} -l -c '{claudeCmd}' in {workspace}");

// Verify the handshake. The only input is Enter, retried until connected, to clear the first-run "trust this
// folder?" prompt — not keystroke-timed TUI puppeteering.
var start = DateTime.UtcNow;
var deadline = start.AddSeconds(seconds);
double lastTrustEnter = 0.0;
while (DateTime.UtcNow < deadline) {
	await Task.Delay(500);
	double elapsed = (DateTime.UtcNow - start).TotalSeconds;

	if (!flags.Connected && elapsed > 3 && elapsed - lastTrustEnter > 2) {
		terminal.Write("\r"u8.ToArray()); // accept "Yes, I trust this folder"
		lastTrustEnter = elapsed;
	}

	if (flags.Connected && flags.ToolsListed && elapsed > 6) {
		break; // handshake confirmed — done
	}
}

Console.WriteLine();
Console.WriteLine("=== HANDSHAKE SUMMARY (real claude vs our IDE-MCP server) ===");
Console.WriteLine($"  client connected+authed : {flags.Connected}");
Console.WriteLine($"  initialize received     : {flags.Initialized}");
Console.WriteLine($"  tools/list received     : {flags.ToolsListed}");
Console.WriteLine($"  raw PTY log             : {ptyLogPath}");
Console.WriteLine("  (openDiff correctness is covered by McpServerTests; live openDiff runs in the app)");

// Tail of claude's rendered output (ANSI stripped) for context.
ptyLog.Flush();
byte[] raw = await File.ReadAllBytesAsync(ptyLogPath);
string text = ansi.Replace(Encoding.UTF8.GetString(raw), string.Empty);
text = new string([.. text.Where(c => c is '\n' or '\t' or >= ' ' and < ((char)127))]);
text = Regex.Replace(text, "[ \t]{2,}", " ");
Console.WriteLine("--- claude output (stripped, last 800 chars) ---");
Console.WriteLine(text.Length > 800 ? text[^800..] : text);

return flags.Connected && flags.Initialized ? 0 : 1;

internal sealed class HandshakeFlags {
	public bool Connected;
	public bool Initialized;
	public bool ToolsListed;
}

internal sealed class LockCleanup(int port) : IDisposable {
	public void Dispose() => IdeLockFile.Delete(port);
}
