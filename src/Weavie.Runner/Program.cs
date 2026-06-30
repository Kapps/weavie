using Weavie.Runner;

var (options, optionsError) = RunnerOptions.Resolve(args);
if (options is null) {
	Console.Error.WriteLine($"[weavie-runner] {optionsError}");
	return 1;
}

if (!Directory.Exists(Path.Combine(options.WorkspaceRoot, ".git"))) {
	Console.WriteLine($"[weavie-runner] note: {options.WorkspaceRoot} is not a git repo — New Session (worktrees) needs git.");
}

// Print identity before anything that can block (the tailscale front shells out), so a stall is always visible.
Console.WriteLine($"[weavie-runner] workspace: {options.WorkspaceRoot}");
Console.WriteLine($"[weavie-runner] tls: {options.Tls.ToString().ToLowerInvariant()}");
Console.Out.Flush();

// The TLS front owns where the endpoints bind and how /backend's URL is built. Tailscale mode sets up
// `tailscale serve` here, so creation can fail loudly (and exit) when the daemon can't be driven.
ITlsFront builtFront;
try {
	builtFront = TlsFronts.Create(options, new TailscaleCli(), line => Log($"[tls] {line}"));
} catch (InvalidOperationException ex) {
	Console.Error.WriteLine($"[weavie-runner] TLS setup failed: {ex.Message}");
	return 1;
}

await using var front = builtFront;

var launcher = new HeadlessLauncher(options, front.WorkerBindAddress, entry =>
	Log($"[{entry.Name}] {entry.Level.ToString().ToLowerInvariant()}: {entry.Message}"));
await using var backends = new BackendManager(options, launcher);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://{front.ControlBindAddress}:{options.Port}");
var app = builder.Build();

// The token-gated control plane makes a permissive CORS origin acceptable; answer the preflight and echo
// the headers. (TLS termination is the front's job — see docs/specs/tls-on-the-runner.md.)
app.Use(async (context, next) => {
	context.Response.Headers.AccessControlAllowOrigin = "*";
	context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type";
	context.Response.Headers.AccessControlAllowMethods = "GET, OPTIONS";
	if (HttpMethods.IsOptions(context.Request.Method)) {
		context.Response.StatusCode = StatusCodes.Status204NoContent;
		return;
	}

	await next().ConfigureAwait(false);
});

ControlApi.Map(app, backends, options, front);

// Start the workspace backend eagerly so the first connection is ready.
var backend = backends.Ensure();

Console.WriteLine($"[weavie-runner] worker headless: {options.HeadlessPath} (port {backend.Port})");
Console.WriteLine($"[weavie-runner] control plane: {front.RegisterUrl}");
Console.WriteLine($"[weavie-runner] runner token: {options.RunnerToken}");
Console.Out.Flush();

await app.RunAsync().ConfigureAwait(false);
return 0;

static void Log(string line) {
	Console.WriteLine(line);
	Console.Out.Flush();
}
