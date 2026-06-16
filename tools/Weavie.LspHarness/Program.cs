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

var selector = Environment.GetEnvironmentVariable("WEAVIE_LSP_SERVER");
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

var workspace = Environment.GetEnvironmentVariable("WEAVIE_LSP_WORKSPACE");
var workspaceIsTemp = string.IsNullOrEmpty(workspace);
if (workspaceIsTemp) {
	workspace = Path.Combine(Path.GetTempPath(), "weavie-lsp-harness");
	Directory.CreateDirectory(workspace);
	File.WriteAllText(Path.Combine(workspace, "tsconfig.json"),
		"{\n  \"compilerOptions\": { \"strict\": true, \"target\": \"ESNext\", \"module\": \"ESNext\", \"moduleResolution\": \"Bundler\", \"noEmit\": true }\n}\n");
	File.WriteAllText(Path.Combine(workspace, "sample.ts"), SampleSource);
}

var samplePath = Path.Combine(workspace!, "sample.ts");
var rootUri = new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri;
var fileUri = new Uri(samplePath).AbsoluteUri;
Console.WriteLine($"[lsp-harness] workspace: {workspace}");

var token = IdeLockFile.NewAuthToken();
// allowedOrigin: null — the harness is a native client and sends no Origin header; the token is the gate.
await using var bridge = new LspBridgeServer(token, workspace!, allowedOrigin: null);
bridge.Log += line => Console.WriteLine($"[bridge] {line}");
var port = bridge.Start();

var results = new Results();
using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(100));
var ct = overall.Token;

var debug = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_LSP_DEBUG"));
await using var client = new LspTestClient([workspace!], line => Console.WriteLine($"[client] {line}"), debug);
await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/{descriptor.Id}?token={token}"), ct);
Console.WriteLine($"[lsp-harness] connected ws://127.0.0.1:{port}/{descriptor.Id}");

// 1. initialize → inspect server capabilities.
var initResult = await client.RequestAsync("initialize", InitializeParams(rootUri, fileUri), ct);
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
		["languageId"] = "typescript",
		["version"] = 1,
		["text"] = SampleSource,
	},
}, ct);

// The pull requests below (semantic tokens, hover, completion) force vtsls to fully load the
// project; cold start can take ~30s, so we issue them first and check the (server-pushed)
// diagnostics last — by then the project is loaded and the diagnostics have arrived.

// 3. Semantic tokens (the hard requirement).
if (results.SemanticTokensProvider) {
	try {
		var st = await client.RequestAsync("textDocument/semanticTokens/full",
			new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = fileUri } }, ct);
		if (st.ValueKind == JsonValueKind.Object && st.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) {
			results.SemanticTokenInts = data.GetArrayLength();
		}
	} catch (InvalidOperationException ex) {
		Console.Error.WriteLine($"[lsp-harness] semanticTokens: {ex.Message}");
	}
}

// 4. Hover on the `add` function (line 1, char 9).
try {
	var hover = await client.RequestAsync("textDocument/hover", PositionParams(fileUri, 1, 9), ct);
	results.HoverHasContents = hover.ValueKind == JsonValueKind.Object && hover.TryGetProperty("contents", out _);
} catch (InvalidOperationException ex) {
	Console.Error.WriteLine($"[lsp-harness] hover: {ex.Message}");
}

// 5. Completion inside the function body (line 2, char 10).
try {
	var completion = await client.RequestAsync("textDocument/completion", PositionParams(fileUri, 2, 10), ct);
	results.CompletionItems = CountCompletions(completion);
} catch (InvalidOperationException ex) {
	Console.Error.WriteLine($"[lsp-harness] completion: {ex.Message}");
}

// 6. Diagnostics. tsserver-based servers (vtsls) PUSH via publishDiagnostics; ts-go / TS 7 uses the
// 3.17 PULL model (textDocument/diagnostic). Support both (spec §15). The sample has a type error.
try {
	JsonElement diagnostics;
	if (results.DiagnosticProvider) {
		var report = await client.RequestAsync("textDocument/diagnostic",
			new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = fileUri } }, ct);
		diagnostics = report.ValueKind == JsonValueKind.Object && report.TryGetProperty("items", out var items)
			? items
			: default;
	} else {
		diagnostics = await client.WaitForDiagnosticsAsync(fileUri, TimeSpan.FromSeconds(30), ct);
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

await client.NotifyAsync("textDocument/didClose", new JsonObject {
	["textDocument"] = new JsonObject { ["uri"] = fileUri },
}, ct);

results.Print();
return results.Passed ? 0 : 1;

static JsonObject InitializeParams(string rootUri, string fileUri) => new() {
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
	["initializationOptions"] = new JsonObject(),
};

static JsonObject PositionParams(string uri, int line, int character) => new() {
	["textDocument"] = new JsonObject { ["uri"] = uri },
	["position"] = new JsonObject { ["line"] = line, ["character"] = character },
};

static JsonArray ToArray(IEnumerable<string> values) {
	var array = new JsonArray();
	foreach (var v in values) {
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
	internal const string SampleSource =
		"const greeting: number = \"hello\";\n" +
		"function add(a: number, b: number): number {\n" +
		"  return a + b;\n" +
		"}\n" +
		"class Point {\n" +
		"  x = 0;\n" +
		"  y = 0;\n" +
		"}\n";

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
		Console.WriteLine($"  RESULT                 : {(Passed ? "PASS" : "FAIL")}");
	}
}
