using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Lsp;
using Weavie.Core.Mcp;
using Weavie.LspHarness;

// Dev harness (NOT shipped): proves the LSP bridge end-to-end against a real, mature TypeScript
// server (vtsls / typescript-language-server). It stands up the host-side LspBridgeServer, connects a
// loopback WebSocket LSP client (standing in for monaco-languageclient), and verifies the M0
// "done when": diagnostics, semantic tokens, hover, and completion are live on a real .ts file —
// all without the WebView. The server/transport half of M0 is thus deterministically CI-checkable.
//   WEAVIE_LSP_SERVER     selector / language id (default "typescript")
//   WEAVIE_LSP_WORKSPACE  workspace dir (default a fresh temp dir with a tsconfig + sample.ts)

string? selector = Environment.GetEnvironmentVariable("WEAVIE_LSP_SERVER");
selector = string.IsNullOrEmpty(selector) ? "typescript" : selector;

var descriptor = LanguageServerCatalog.ForLanguage(selector) ?? LanguageServerCatalog.ForServerId(selector);
if (descriptor is null) {
	Console.Error.WriteLine($"[lsp-harness] no recipe for selector '{selector}'");
	return 2;
}

var resolved = ServerResolver.Resolve(descriptor);
if (resolved is null) {
	Console.Error.WriteLine($"[lsp-harness] SKIP: no server for {descriptor.DisplayName} on PATH " +
		$"(tried {string.Join(", ", descriptor.Candidates.Select(c => c.Command))}).");
	return 2;
}

Console.WriteLine($"[lsp-harness] server: {resolved.ServerPath}");

var probe = LanguageProbe.For(descriptor.Id) ?? LanguageProbe.For(selector);
if (probe is null) {
	Console.Error.WriteLine($"[lsp-harness] no probe fixture for '{descriptor.Id}'");
	return 2;
}

string? workspace = Environment.GetEnvironmentVariable("WEAVIE_LSP_WORKSPACE");
bool workspaceIsTemp = string.IsNullOrEmpty(workspace);
if (workspaceIsTemp) {
	workspace = Path.Combine(Path.GetTempPath(), $"weavie-lsp-harness-{descriptor.Id}");
	Directory.CreateDirectory(workspace);
	foreach (var (name, content) in probe.ProjectFiles) {
		File.WriteAllText(Path.Combine(workspace, name), content);
	}

	File.WriteAllText(Path.Combine(workspace, probe.MainFileName), probe.Source);
}

string samplePath = Path.Combine(workspace!, probe.MainFileName);
string rootUri = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri;
string fileUri = new Uri(samplePath).AbsoluteUri;
Console.WriteLine($"[lsp-harness] workspace: {workspace}");

string token = IdeLockFile.NewAuthToken();
// allowedOrigin: null — the harness is a native client and sends no Origin header; the token is the gate.
await using var bridge = new LspBridgeServer(token, workspace!, allowedOrigin: null, resolveDescriptor: null);
bool watchBroadcast = false;
bridge.Log += line => {
	Console.WriteLine($"[bridge] {line}");
	if (line.Contains("didChangeWatchedFiles", StringComparison.Ordinal)) {
		watchBroadcast = true;
	}
};
int port = bridge.Start();

var results = new Results();
// Generous: csharp-ls runs a design-time MSBuild load and gopls indexes the module on first open.
using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(150));
var ct = overall.Token;

bool debug = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_LSP_DEBUG"));
var defaultSettings = string.IsNullOrEmpty(descriptor.DefaultSettingsJson) ? null : JsonNode.Parse(descriptor.DefaultSettingsJson);
await using var client = new LspTestClient([workspace!], line => Console.WriteLine($"[client] {line}"), debug, defaultSettings?.DeepClone());
await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/{descriptor.Id}?token={token}"), ct);
Console.WriteLine($"[lsp-harness] connected ws://127.0.0.1:{port}/{descriptor.Id}");

// 1. initialize → inspect server capabilities.
var initResult = await client.RequestAsync("initialize", InitializeParams(rootUri, defaultSettings?.DeepClone()), ct);
var caps = initResult.GetProperty("capabilities");
if (debug) {
	Console.WriteLine($"[lsp-harness] capabilities keys: {string.Join(", ", caps.EnumerateObject().Select(p => p.Name))}");
	if (caps.TryGetProperty("diagnosticProvider", out var dp)) {
		Console.WriteLine($"[lsp-harness] diagnosticProvider: {dp.GetRawText()}");
	}
}
results.SemanticTokensProvider = caps.TryGetProperty("semanticTokensProvider", out var stp) && stp.ValueKind != JsonValueKind.Null;
results.HoverProvider = caps.TryGetProperty("hoverProvider", out _);
results.CompletionProvider = caps.TryGetProperty("completionProvider", out _);
results.DiagnosticProvider = caps.TryGetProperty("diagnosticProvider", out var diag) && diag.ValueKind != JsonValueKind.Null;
if (results.SemanticTokensProvider && stp.TryGetProperty("legend", out var legend) && legend.TryGetProperty("tokenTypes", out var tt)) {
	results.LegendTokenTypeCount = tt.GetArrayLength();
}

await client.NotifyAsync("initialized", new JsonObject(), ct);

// 2. didOpen the sample → triggers diagnostics + indexing.
await client.NotifyAsync("textDocument/didOpen", new JsonObject {
	["textDocument"] = new JsonObject {
		["uri"] = fileUri,
		["languageId"] = probe.LanguageId,
		["version"] = 1,
		["text"] = probe.Source,
	},
}, ct);

// The pull requests below (semantic tokens, hover, completion) force vtsls to fully load the
// project; cold start can take ~30s, so we issue them first and check the (server-pushed)
// diagnostics last — by then the project is loaded and the diagnostics have arrived.

// 3. Semantic tokens (the hard requirement). Attempt regardless of the static capability — servers
// like csharp-ls advertise it via dynamic client/registerCapability, not the initialize result.
try {
	var st = await client.RequestAsync("textDocument/semanticTokens/full",
		new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = fileUri } }, ct);
	if (st.ValueKind == JsonValueKind.Object && st.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) {
		results.SemanticTokenInts = data.GetArrayLength();
	}
} catch (InvalidOperationException ex) {
	Console.Error.WriteLine($"[lsp-harness] semanticTokens: {ex.Message}");
}
results.SemanticTokensProvider = results.SemanticTokensProvider || client.IsRegistered("textDocument/semanticTokens") || results.SemanticTokenInts > 0;

// 4. Hover on the `add` function (line 1, char 9).
try {
	var hover = await client.RequestAsync("textDocument/hover", PositionParams(fileUri, probe.HoverLine, probe.HoverChar), ct);
	results.HoverHasContents = hover.ValueKind == JsonValueKind.Object && hover.TryGetProperty("contents", out _);
} catch (InvalidOperationException ex) {
	Console.Error.WriteLine($"[lsp-harness] hover: {ex.Message}");
}

// 5. Completion inside the function body (line 2, char 10).
try {
	var completion = await client.RequestAsync("textDocument/completion", PositionParams(fileUri, probe.CompletionLine, probe.CompletionChar), ct);
	results.CompletionItems = CountCompletions(completion);
} catch (InvalidOperationException ex) {
	Console.Error.WriteLine($"[lsp-harness] completion: {ex.Message}");
}
// Reflect dynamic registration (csharp-ls registers completion via client/registerCapability).
results.CompletionProvider = results.CompletionProvider || client.IsRegistered("textDocument/completion") || results.CompletionItems > 0;

// 6. Diagnostics. tsserver-based servers (vtsls) PUSH via publishDiagnostics; ts-go / TS 7 uses the
// 3.17 PULL model (textDocument/diagnostic). Support both (spec §15). The sample has a type error.
bool canPull = results.DiagnosticProvider || client.IsRegistered("textDocument/diagnostic");
results.DiagnosticProvider = canPull;
try {
	JsonElement diagnostics;
	if (canPull) {
		var report = await client.RequestAsync("textDocument/diagnostic",
			new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = fileUri } }, ct);
		diagnostics = report.ValueKind == JsonValueKind.Object && report.TryGetProperty("items", out var items)
			? items
			: default;
	} else {
		diagnostics = await client.WaitForDiagnosticsAsync(fileUri, TimeSpan.FromSeconds(60), ct);
	}

	if (diagnostics.ValueKind == JsonValueKind.Array) {
		results.DiagnosticCount = diagnostics.GetArrayLength();
		if (results.DiagnosticCount > 0) {
			results.FirstDiagnostic = diagnostics[0].GetProperty("message").GetString();
		}
	}
} catch (TimeoutException ex) {
	Console.Error.WriteLine($"[lsp-harness] diagnostics: {ex.Message}");
} catch (InvalidOperationException ex) {
	Console.Error.WriteLine($"[lsp-harness] diagnostics: {ex.Message}");
}

// 7. File-watcher → didChangeWatchedFiles (§9). Touch a watched file on disk and confirm the host
// detects it and broadcasts to the live server (the agentic-editor path: Claude edits files on disk).
if (workspaceIsTemp) {
	try {
		File.WriteAllText(Path.Combine(workspace!, $"watched-trigger{Path.GetExtension(probe.MainFileName)}"), probe.Source);
		using var watchWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
		watchWait.CancelAfter(TimeSpan.FromSeconds(5));
		while (!watchBroadcast && !watchWait.IsCancellationRequested) {
			await Task.Delay(100, watchWait.Token);
		}
	} catch (OperationCanceledException) {
		// timed out waiting for the broadcast
	}
}

results.WatchBroadcast = watchBroadcast;

await client.NotifyAsync("textDocument/didClose", new JsonObject {
	["textDocument"] = new JsonObject { ["uri"] = fileUri },
}, ct);

results.Print();
return results.Passed ? 0 : 1;

static JsonObject InitializeParams(string rootUri, JsonNode? initializationOptions) => new() {
	["processId"] = Environment.ProcessId,
	["clientInfo"] = new JsonObject { ["name"] = "weavie-lsp-harness", ["version"] = "0.1.0" },
	["rootUri"] = rootUri,
	["workspaceFolders"] = new JsonArray { new JsonObject { ["uri"] = rootUri, ["name"] = "weavie" } },
	["capabilities"] = new JsonObject {
		["workspace"] = new JsonObject {
			["configuration"] = true,
			["workspaceFolders"] = true,
			["didChangeConfiguration"] = new JsonObject { ["dynamicRegistration"] = true },
		},
		["textDocument"] = new JsonObject {
			["synchronization"] = new JsonObject { ["didSave"] = true, ["dynamicRegistration"] = true },
			["publishDiagnostics"] = new JsonObject { ["relatedInformation"] = true },
			["diagnostic"] = new JsonObject { ["dynamicRegistration"] = true, ["relatedDocumentSupport"] = false },
			["hover"] = new JsonObject { ["contentFormat"] = new JsonArray { "markdown", "plaintext" } },
			["completion"] = new JsonObject {
				["dynamicRegistration"] = true,
				["completionItem"] = new JsonObject { ["snippetSupport"] = true },
			},
			["semanticTokens"] = new JsonObject {
				["dynamicRegistration"] = true,
				["requests"] = new JsonObject { ["range"] = true, ["full"] = new JsonObject { ["delta"] = true } },
				["tokenTypes"] = ToArray(SemanticTokenTypes),
				["tokenModifiers"] = ToArray(SemanticTokenModifiers),
				["formats"] = new JsonArray { "relative" },
				["overlappingTokenSupport"] = false,
				["multilineTokenSupport"] = false,
			},
		},
	},
	["initializationOptions"] = initializationOptions ?? new JsonObject(),
};

static JsonObject PositionParams(string uri, int line, int character) => new() {
	["textDocument"] = new JsonObject { ["uri"] = uri },
	["position"] = new JsonObject { ["line"] = line, ["character"] = character },
};

static JsonArray ToArray(IEnumerable<string> values) {
	var array = new JsonArray();
	foreach (string v in values) {
		array.Add(v);
	}

	return array;
}

static int CountCompletions(JsonElement completion) {
	if (completion.ValueKind == JsonValueKind.Array) {
		return completion.GetArrayLength();
	}

	if (completion.ValueKind == JsonValueKind.Object && completion.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array) {
		return items.GetArrayLength();
	}

	return 0;
}

internal partial class Program {
	// Standard LSP semantic token legend (declared so servers advertise semanticTokensProvider).
	internal static readonly string[] SemanticTokenTypes = [
		"namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter",
		"variable", "property", "enumMember", "event", "function", "method", "macro", "keyword",
		"modifier", "comment", "string", "number", "regexp", "operator", "decorator",
	];

	internal static readonly string[] SemanticTokenModifiers = [
		"declaration", "definition", "readonly", "static", "deprecated", "abstract", "async",
		"modification", "documentation", "defaultLibrary",
	];
}

internal sealed class Results {
	public bool SemanticTokensProvider;
	public int LegendTokenTypeCount;
	public bool HoverProvider;
	public bool CompletionProvider;
	public bool DiagnosticProvider;
	public int DiagnosticCount;
	public string? FirstDiagnostic;
	public int SemanticTokenInts;
	public bool HoverHasContents;
	public int CompletionItems;
	public bool WatchBroadcast;

	// The hard M0 gates: a real diagnostic and real semantic tokens prove the compiler-truth pipeline.
	public bool Passed => DiagnosticCount > 0 && SemanticTokenInts > 0 && HoverHasContents && CompletionItems > 0;

	public void Print() {
		Console.WriteLine();
		Console.WriteLine("=== LSP BRIDGE HARNESS SUMMARY (real server via WS↔stdio bridge) ===");
		Console.WriteLine($"  semanticTokensProvider : {SemanticTokensProvider} (legend types: {LegendTokenTypeCount})");
		Console.WriteLine($"  hoverProvider          : {HoverProvider}");
		Console.WriteLine($"  completionProvider     : {CompletionProvider}");
		Console.WriteLine($"  diagnostics            : {DiagnosticCount}  ({(DiagnosticProvider ? "pull" : "push")}; first: {FirstDiagnostic ?? "-"})");
		Console.WriteLine($"  semanticTokens ints    : {SemanticTokenInts}  ({SemanticTokenInts / 5} tokens)");
		Console.WriteLine($"  hover has contents     : {HoverHasContents}");
		Console.WriteLine($"  completion items       : {CompletionItems}");
		Console.WriteLine($"  file-watch → server    : {WatchBroadcast}");
		Console.WriteLine($"  RESULT                 : {(Passed ? "PASS" : "FAIL")}");
	}
}
