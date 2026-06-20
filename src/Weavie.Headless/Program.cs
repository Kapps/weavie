using System.Text;
using Microsoft.Extensions.FileProviders;
using Weavie.Core.Hooks;
using Weavie.Headless;
using Weavie.Hosting;

// Hook relay (no UI): the spawned claude runs this exe as its PreToolUse/PostToolUse command hook; forward
// the event over the pipe to the running instance and echo its decision. Must branch before anything else.
if (args.Length > 0 && args[0] == "--hook-relay") {
	return HookRelayClient.Run();
}

string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
int port = ResolvePort(args);
string bind = ResolveBind(args);
string? token = ResolveToken(args);
string workspaceOverride = ResolveWorkspace(args);

// Fail closed: a non-loopback bind exposes the bridge to the network, so it MUST carry a token. Only a
// loopback bind (local dev / `npm run capture`) may run untokenized — there the OS loopback is the boundary.
// This makes "exposed but unauthenticated" impossible regardless of how the two flags were passed.
if (!IsLoopbackBind(bind) && string.IsNullOrEmpty(token)) {
	Console.Error.WriteLine(
		$"[weavie-headless] refusing to bind non-loopback interface '{bind}' without a token — "
		+ "pass --token <t> (or WEAVIE_SERVE_TOKEN), or bind 127.0.0.1 for local-only.");
	return 1;
}

// The host outlives any one page: build it once, then let each browser connection (re)attach its socket.
var bridge = new WebSocketHostBridge();
var services = HostServices.CreateDefault();
string workspace = !string.IsNullOrEmpty(workspaceOverride)
	? workspaceOverride
	: services.Settings.GetString("workspace") ?? Environment.CurrentDirectory;
await using var core = new HostCore(new HeadlessPlatform(bridge), services, workspace);
// The page connects to this loopback origin; the LSP/MCP servers pin it as their allowed origin.
await core.StartAsync($"http://127.0.0.1:{port}").ConfigureAwait(false);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // We print our own concise status; Kestrel's request logging is noise here.
builder.WebHost.UseUrls($"http://{bind}:{port}");
var app = builder.Build();

app.UseWebSockets();

var assets = Directory.Exists(wwwroot) ? new PhysicalFileProvider(wwwroot) : null;

// SINGLE auth gate, default-deny. When a token is configured (any network-exposed host — enforced at
// startup), EVERY request must carry the token EXCEPT public static assets (the JS/CSS the gated document
// loads, which hold no secrets). So the document, the bridge, and any endpoint added later are gated
// automatically — you can't forget a per-route check, because there are none. "Public" is the narrow, exact
// exception: a real file under wwwroot that isn't index.html. Everything else (incl. unknown paths) → 401.
if (token is not null) {
	app.Use(async (context, next) => {
		var path = context.Request.Path;
		bool publicAsset = assets is not null
			&& path != "/" && path != "/index.html"
			&& assets.GetFileInfo(path.Value ?? "/").Exists;
		if (publicAsset || TokenMatches(context, token)) {
			await next().ConfigureAwait(false);
			return;
		}

		context.Response.StatusCode = StatusCodes.Status401Unauthorized;
	});
}

// Serve index.html ourselves so we can inject the bootstrap globals (bridge URL + fonts + commands) before
// the module graph runs — must run before the static middleware, which would otherwise serve it verbatim.
// Auth is already enforced centrally above (the document is not a public asset, so it required the token).
app.Use(async (context, next) => {
	string path = context.Request.Path.Value ?? "/";
	if (path is "/" or "/index.html") {
		await ServeIndexAsync(context, wwwroot, core).ConfigureAwait(false);
		return;
	}

	await next().ConfigureAwait(false);
});

if (assets is not null) {
	app.UseStaticFiles(new StaticFileOptions { FileProvider = assets });
}

// The bridge endpoint: each browser connection drives the (single, long-lived) headless session. Auth is
// enforced by the central gate above (the bridge path is not a public asset, so it required the token).
app.Map("/weavie-bridge", async context => {
	if (!context.WebSockets.IsWebSocketRequest) {
		context.Response.StatusCode = StatusCodes.Status400BadRequest;
		return;
	}

	using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
	await bridge.ServeAsync(socket, context.RequestAborted).ConfigureAwait(false);
});

string shownHost = bind is "0.0.0.0" or "::" ? "127.0.0.1" : bind;
string tokenSuffix = token is null ? string.Empty : $"/?token={token}";
Console.WriteLine($"[weavie-headless] workspace: {core.WorkspaceRoot}");
Console.WriteLine($"[weavie-headless] open  http://{shownHost}:{port}{tokenSuffix}  in a browser");
Console.Out.Flush();
await app.RunAsync().ConfigureAwait(false);

// Best-effort cleanup of the app-global stores' file watchers on shutdown (core disposes the sessions).
services.Keybindings.Dispose();
services.Settings.Dispose();
return 0;

static int ResolvePort(string[] args) {
	for (int i = 0; i < args.Length - 1; i++) {
		if (args[i] == "--port" && int.TryParse(args[i + 1], out int parsed)) {
			return parsed;
		}
	}

	return int.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SERVE_PORT"), out int fromEnv) ? fromEnv : 8700;
}

static string ResolveBind(string[] args) {
	for (int i = 0; i < args.Length - 1; i++) {
		if (args[i] == "--bind") {
			return args[i + 1];
		}
	}

	// Default to loopback so a bare `Weavie.Headless` stays local; the runner passes --bind to expose workers.
	return Environment.GetEnvironmentVariable("WEAVIE_SERVE_BIND") ?? "127.0.0.1";
}

static string? ResolveToken(string[] args) {
	for (int i = 0; i < args.Length - 1; i++) {
		if (args[i] == "--token") {
			return args[i + 1];
		}
	}

	string? fromEnv = Environment.GetEnvironmentVariable("WEAVIE_SERVE_TOKEN");
	return string.IsNullOrEmpty(fromEnv) ? null : fromEnv;
}

static bool IsLoopbackBind(string bind) =>
	bind is "127.0.0.1" or "::1" or "localhost"
	|| (System.Net.IPAddress.TryParse(bind, out var ip) && System.Net.IPAddress.IsLoopback(ip));

static bool TokenMatches(HttpContext context, string expected) {
	string presented = context.Request.Query.TryGetValue("token", out var t) ? t.ToString() : string.Empty;
	if (presented.Length != expected.Length) {
		return false;
	}

	int diff = 0;
	for (int i = 0; i < expected.Length; i++) {
		diff |= presented[i] ^ expected[i];
	}

	return diff == 0;
}

static string ResolveWorkspace(string[] args) {
	for (int i = 0; i < args.Length - 1; i++) {
		if (args[i] == "--workspace") {
			return args[i + 1];
		}
	}

	return Environment.GetEnvironmentVariable("WEAVIE_SERVE_WORKSPACE") ?? string.Empty;
}

static async Task ServeIndexAsync(HttpContext context, string wwwroot, HostCore core) {
	string indexPath = Path.Combine(wwwroot, "index.html");
	context.Response.ContentType = "text/html; charset=utf-8";
	if (!File.Exists(indexPath)) {
		await context.Response.WriteAsync(
			"<!doctype html><meta charset=utf-8><body style=\"font-family:sans-serif;padding:2rem\">"
			+ "<h1>weavie headless</h1><p>Web assets not found. Run <code>pnpm run build</code> in "
			+ "<code>src/web</code>, then rebuild the host.</p>").ConfigureAwait(false);
		return;
	}

	string html;
	try {
		html = await File.ReadAllTextAsync(indexPath, Encoding.UTF8).ConfigureAwait(false);
	} catch (IOException ex) {
		context.Response.StatusCode = StatusCodes.Status500InternalServerError;
		await context.Response.WriteAsync($"failed to read index.html: {ex.Message}").ConfigureAwait(false);
		return;
	}

	// The page picks the WebSocket transport from __WEAVIE_BRIDGE_WS__; the rest of the bootstrap is the
	// same content every host injects (fonts/editor/theme/lsp/commands/keybindings + shell config).
	string bootstrap = $"<script>window.__WEAVIE_BRIDGE_WS__ = \"auto\";{core.BuildBootstrap()}</script>";
	// Inject right after <head> so the globals exist before the module graph (the module script is at body end).
	html = html.Contains("<head>", StringComparison.Ordinal)
		? html.Replace("<head>", "<head>" + bootstrap, StringComparison.Ordinal)
		: bootstrap + html;
	await context.Response.WriteAsync(html).ConfigureAwait(false);
}
