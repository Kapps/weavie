using System.Text;
using Microsoft.Extensions.FileProviders;
using Weavie.Headless;
using Weavie.Hosting;

string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
int port = ResolvePort(args);
string workspaceOverride = ResolveWorkspace(args);

// Unsafe combinations fail closed (exit 1); see ListenMode for the auth invariant.
var (listen, listenError) = ListenMode.Resolve(args);
if (listen is null) {
	Console.Error.WriteLine($"[weavie-headless] {listenError}");
	return 1;
}

string bind = listen.BindAddress;

// Remote mode token-gates every request, so the bridge's legitimate client is cross-origin by design (the
// app at https://weavie.app, the runner's picker page on another port). The CSWSH same-origin check below is
// therefore the LOCAL no-token mode's only bridge defense and applies only there.
bool tokenGated = listen is ListenMode.Remote;

// The host outlives any one page: built once, each browser connection (re)attaches its socket.
var bridge = new WebSocketHostBridge();
var services = HostServices.CreateDefault();
// Deterministic Open-PR journeys for the integration harness / capture: a JSON file of canned PRs replaces the
// live GitHub provider, the PR analogue of WEAVIE_FAKE_CLAUDE_SCRIPT. Unset in normal use.
if (Environment.GetEnvironmentVariable("WEAVIE_FAKE_PRS") is { Length: > 0 } fakePrsPath && File.Exists(fakePrsPath)) {
	services = services with { PullRequests = FakePullRequests.FromFile(fakePrsPath) };
}

string workspace = !string.IsNullOrEmpty(workspaceOverride)
	? workspaceOverride
	: services.Settings.GetString("workspace") ?? Environment.CurrentDirectory;
await using var core = new HostCore(new HeadlessPlatform(bridge), services, workspace);
// The page connects to this loopback origin; the LSP/MCP servers pin it as their allowed origin.
await core.StartAsync($"http://127.0.0.1:{port}").ConfigureAwait(false);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // We print our own status; Kestrel request logging is noise here.
builder.WebHost.UseUrls($"http://{bind}:{port}");
var app = builder.Build();

// Keepalive with a timeout so a half-open peer (an unclean refresh / crash / sleep / network drop, which sends
// no close frame) is actively detected and aborted, instead of lingering as a zombie the bridge keeps
// broadcasting to. The per-connection send queues bound the damage either way; this is the prompt detector.
app.UseWebSockets(new WebSocketOptions {
	KeepAliveInterval = TimeSpan.FromSeconds(30),
	KeepAliveTimeout = TimeSpan.FromSeconds(30),
});

var assets = Directory.Exists(wwwroot) ? new PhysicalFileProvider(wwwroot) : null;

// Default-deny auth gate, active only in remote mode: every request needs the token except public static
// assets (real wwwroot files other than index.html, which hold no secrets).
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

// Serve index.html ourselves to inject the bootstrap globals before the module graph runs; must precede
// the static middleware, which would otherwise serve it verbatim.
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

// The bridge endpoint: each browser connection drives the single long-lived headless session.
app.Map("/weavie-bridge", async context => {
	if (!context.WebSockets.IsWebSocketRequest) {
		context.Response.StatusCode = StatusCodes.Status400BadRequest;
		return;
	}

	// CSWSH guard for the LOCAL no-token mode only — there the bridge has no other auth, so a cross-origin
	// browser upgrade (Origin authority ≠ our own Host) is a foreign page and is rejected. In remote mode the
	// token already gated this request and the real client is cross-origin by design, so the check must not run.
	if (!tokenGated && !SameOriginOrNonBrowser(context.Request)) {
		context.Response.StatusCode = StatusCodes.Status403Forbidden;
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

// Best-effort cleanup of the app-global stores' file watchers (core disposes the sessions).
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

// A WS upgrade is same-origin (Origin authority == this server's Host) or has no Origin (a non-browser client,
// which carries no ambient credentials so isn't a CSWSH vector). Anything else is a foreign page → reject.
static bool SameOriginOrNonBrowser(HttpRequest request) {
	string origin = request.Headers.Origin.ToString();
	if (string.IsNullOrEmpty(origin)) {
		return true;
	}

	return Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
		&& string.Equals(parsed.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
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

	// The page picks the WebSocket transport from __WEAVIE_BRIDGE_WS__; the rest is the shared host bootstrap.
	string bootstrap = $"<script>window.__WEAVIE_BRIDGE_WS__ = \"auto\";{core.BuildBootstrap()}</script>";
	html = html.Contains("<head>", StringComparison.Ordinal)
		? html.Replace("<head>", "<head>" + bootstrap, StringComparison.Ordinal)
		: bootstrap + html;
	await context.Response.WriteAsync(html).ConfigureAwait(false);
}
