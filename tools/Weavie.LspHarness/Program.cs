using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Lsp;
using Weavie.Hosting;
using Weavie.LspHarness;

// Dev harness (not shipped): proves the LSP bridge end-to-end against a real language server. It drives a real
// LspController over a fake IHostBridge (the same path the WebView takes — lsp-start/lsp-data/lsp-stop, no
// socket), with an in-process LSP client standing in for monaco-languageclient, and verifies that diagnostics,
// semantic tokens, hover, and completion are live on a real source file — deterministically CI-checkable.
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

var results = new Results();
// Generous: csharp-ls runs a design-time MSBuild load and gopls indexes the module on first open.
using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(150));
var ct = overall.Token;

bool debug = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_LSP_DEBUG"));
var defaultSettings = string.IsNullOrEmpty(descriptor.DefaultSettingsJson) ? null : JsonNode.Parse(descriptor.DefaultSettingsJson);

// Drive the production LspController over a fake bridge: lsp-data frames it posts are fed straight into the
// in-process client; an lsp-exit means the server died or failed to start.
const string slot = "harness";
const string channel = "harness-1";
LspTestClient? client = null;
var bridge = new HarnessBridge(json => {
	using var doc = JsonDocument.Parse(json);
	var root = doc.RootElement;
	string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
	if (type == "lsp-data" && root.TryGetProperty("payload", out var payload)) {
		client?.Deliver(System.Text.Encoding.UTF8.GetBytes(payload.GetRawText()));
	} else if (type == "lsp-exit") {
		Console.WriteLine($"[bridge] lsp-exit: {(root.TryGetProperty("reason", out var r) ? r.GetString() : null)} (code {(root.TryGetProperty("code", out var c) ? c.GetInt32() : 0)})");
	}
});
await using var controller = new LspController(
	bridge, workspace!, new LspServerLauncher(), LanguageServerCatalog.Resolve, line => Console.WriteLine($"[lsp] {line}"));

// The workspace watcher lives on the host session in production; stand up an equivalent so the file-watch →
// didChangeWatchedFiles path (§9) is exercised the same way.
bool watchBroadcast = false;
using var watcher = new WorkspaceWatcher(workspace!, LanguageServerCatalog.WatchedExtensions, batch => {
	controller.NotifyWatchedFileChanges(batch);
	if (batch.Count > 0) {
		watchBroadcast = true;
	}
}, line => Console.WriteLine($"[watch] {line}"), 250);
watcher.Start();

await using var clientHandle = client = new LspTestClient(
	[workspace!], line => Console.WriteLine($"[client] {line}"), debug, defaultSettings?.DeepClone(),
	frame => controller.Data(channel, frame));
client.Start();
controller.Start(slot, descriptor.Id, channel);
Console.WriteLine($"[lsp-harness] started {descriptor.Id} over the in-process bridge (channel {channel})");

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

// The requests below (semantic tokens, hover, completion) force the server to fully load the project, so we
// issue them first and check the server-pushed diagnostics last, by which point they've arrived.

// 3. Semantic tokens (the hard requirement). Attempt regardless of the static capability — servers like
// csharp-ls advertise it via dynamic client/registerCapability, not the initialize result.
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

// 6. Diagnostics. Some servers push via publishDiagnostics; others use the 3.17 pull model
// (textDocument/diagnostic). Support both (spec §15). The sample has a type error.
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

// 7. File-watcher → didChangeWatchedFiles (§9). Touch a file on disk and confirm the host detects it and
// broadcasts to the live server (the agentic-editor path: Claude edits files on disk).
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
	// Standard LSP semantic token legend, declared so servers advertise semanticTokensProvider.
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

// A do-nothing IHostBridge that hands every host→web message to a callback — lets the harness watch the
// controller's lsp-data/lsp-exit without a WebView. Inbound (web→host) is driven directly via the controller.
internal sealed class HarnessBridge(Action<string> onPost) : IHostBridge {
	public event Action<string>? MessageReceived { add { } remove { } }

	public void PostToWeb(string json) => onPost(json);
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
		Console.WriteLine("=== LSP BRIDGE HARNESS SUMMARY (real server via the in-process bridge) ===");
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
