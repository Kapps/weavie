using System.Text;
using Microsoft.Extensions.FileProviders;
using Weavie.Headless;
using Weavie.Hosting;

string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
int port = ResolvePort(args);
string workspaceOverride = ResolveWorkspace(args);

// Resolve the listening mode ONCE, up front. Remote listening is opt-in (--remote) and, when on, the
// ListenMode.Remote case carries a required token — so auth keys off the MODE below, never off "is a token
// present." A network interface can be bound only via Remote (which mandates the token), so an exposed
// untokenized host can't be expressed. A contradictory/unsafe combination fails closed here (exit 1).
var (listen, listenError) = ListenMode.Resolve(args);
if (listen is null) {
	Console.Error.WriteLine($"[weavie-headless] {listenError}");
	return 1;
}

string bind = listen.BindAddress;

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

// SINGLE auth gate, default-deny — enabled by the listening MODE, not by token presence. In remote mode
// (the only mode that binds the network) EVERY request must carry the required token EXCEPT public static
// assets (the JS/CSS the gated document loads, which hold no secrets). So the document, the bridge, and any
// endpoint added later are gated automatically — there are no per-route checks to forget. "Public" is the
// narrow, exact exception: a real file under wwwroot that isn't index.html. Local mode never binds the
// network, so it needs no gate (the OS loopback is the boundary).
if (listen is ListenMode.Remote remote) {
	string authToken = remote.Token;
	app.Use(async (context, next) => {
		var path = context.Request.Path;
		bool publicAsset = assets is not null
			&& path != "/" && path != "/index.html"
			&& assets.GetFileInfo(path.Value ?? "/").Exists;
		if (publicAsset || TokenMatches(context, authToken)) {
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
string tokenSuffix = listen is ListenMode.Remote shown ? $"/?token={shown.Token}" : string.Empty;
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
