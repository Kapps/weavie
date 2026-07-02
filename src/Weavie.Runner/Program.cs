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

// Auto-update: workers spawn from the managed layout's resolved `current` version once one is staged;
// until then (and always, with the flag off) they spawn the co-located build the options resolved.
var versions = options.AutoUpdate ? VersionStore.Open(line => Log($"[update] {line}")) : null;
Func<string> workerPath = versions is { } store
	? () => store.ActiveWorkerPath() ?? options.HeadlessPath
	: () => options.HeadlessPath;

var launcher = new HeadlessLauncher(workerPath, front.WorkerBindAddress, entry =>
	Log($"[{entry.Name}] {entry.Level.ToString().ToLowerInvariant()}: {entry.Message}"));
await using var backends = new BackendManager(options, launcher, front.WorkerBindAddress);

using var updater = versions is { } activeStore
	? new UpdatePoller(options, activeStore, backends, line => Log($"[update] {line}"))
	: null;
Func<UpdateStatus> updateStatus = updater is { } poller ? poller.Snapshot : UpdatePoller.Disabled;

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

ControlApi.Map(app, backends, options, front, updateStatus);

// Start the workspace backend eagerly so the first connection is ready.
var backend = backends.Ensure();

// After Ensure: the poller's boot reconcile (a runner that died mid-update) must see the spawned
// worker to drain/confirm/roll it back.
updater?.Start();

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
