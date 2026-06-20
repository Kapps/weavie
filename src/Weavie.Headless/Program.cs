using System.Text;
using Microsoft.Extensions.FileProviders;
using Weavie.Core.Hooks;
using Weavie.Headless;

// Hook relay (no UI): the spawned claude runs this exe as its PreToolUse/PostToolUse command hook; forward
// the event over the pipe to the running instance and echo its decision. Must branch before anything else.
if (args.Length > 0 && args[0] == "--hook-relay") {
	return HookRelayClient.Run();
}

string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
int port = ResolvePort(args);
string workspaceOverride = ResolveWorkspace(args);

// The host outlives any one page: build it once, then let each browser connection (re)attach its socket.
var bridge = new WebSocketHostBridge();
await using var session = new HeadlessSession(bridge);
session.Start(workspaceOverride);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // We print our own concise status; Kestrel's request logging is noise here.
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();

app.UseWebSockets();

// Serve index.html ourselves so we can inject the bootstrap globals (bridge URL + fonts + commands) before
// the module graph runs — must run before the static middleware, which would otherwise serve it verbatim.
app.Use(async (context, next) => {
	string path = context.Request.Path.Value ?? "/";
	if (path is "/" or "/index.html") {
		await ServeIndexAsync(context, wwwroot, session).ConfigureAwait(false);
		return;
	}

	await next().ConfigureAwait(false);
});

if (Directory.Exists(wwwroot)) {
	app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(wwwroot) });
}

// The bridge endpoint: each browser connection drives the (single, long-lived) headless session.
app.Map("/weavie-bridge", async context => {
	if (!context.WebSockets.IsWebSocketRequest) {
		context.Response.StatusCode = StatusCodes.Status400BadRequest;
		return;
	}

	using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
	await bridge.ServeAsync(socket, context.RequestAborted).ConfigureAwait(false);
});

Console.WriteLine($"[weavie-headless] workspace: {session.Workspace}");
Console.WriteLine($"[weavie-headless] open  http://127.0.0.1:{port}  in a browser");
Console.Out.Flush();
await app.RunAsync().ConfigureAwait(false);
return 0;

static int ResolvePort(string[] args) {
	for (int i = 0; i < args.Length - 1; i++) {
		if (args[i] == "--port" && int.TryParse(args[i + 1], out int parsed)) {
			return parsed;
		}
	}

	return int.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SERVE_PORT"), out int fromEnv) ? fromEnv : 8700;
}

static string ResolveWorkspace(string[] args) {
	for (int i = 0; i < args.Length - 1; i++) {
		if (args[i] == "--workspace") {
			return args[i + 1];
		}
	}

	return Environment.GetEnvironmentVariable("WEAVIE_SERVE_WORKSPACE") ?? string.Empty;
}

static async Task ServeIndexAsync(HttpContext context, string wwwroot, HeadlessSession session) {
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

	string bootstrap = $"<script>{session.BuildBootstrapScript()}</script>";
	// Inject right after <head> so the globals exist before the module graph (the module script is at body end).
	html = html.Contains("<head>", StringComparison.Ordinal)
		? html.Replace("<head>", "<head>" + bootstrap, StringComparison.Ordinal)
		: bootstrap + html;
	await context.Response.WriteAsync(html).ConfigureAwait(false);
}
