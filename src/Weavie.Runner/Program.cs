using Weavie.Runner;

var (options, optionsError) = RunnerOptions.Resolve(args);
if (options is null) {
	Console.Error.WriteLine($"[weavie-runner] {optionsError}");
	return 1;
}

if (!Directory.Exists(Path.Combine(options.WorkspaceRoot, ".git"))) {
	Console.WriteLine($"[weavie-runner] note: {options.WorkspaceRoot} is not a git repo — New Session (worktrees) needs git.");
}

var launcher = new HeadlessLauncher(options, entry =>
	Log($"[{entry.Name}] {entry.Level.ToString().ToLowerInvariant()}: {entry.Message}"));
await using var backends = new BackendManager(options, launcher);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://{options.Bind}:{options.Port}");
var app = builder.Build();

// A cross-origin web app fetches the control plane to resolve the worker URL. The control plane is
// token-gated, so a permissive CORS origin is acceptable; answer the preflight and echo the headers.
// (TLS + tighter origins are hardening — see docs/specs/remote-sessions.md.)
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

ControlApi.Map(app, backends, options);

// Start the workspace backend eagerly so the first connection is ready.
var backend = backends.Ensure();

string shown = options.Bind is "0.0.0.0" or "::" ? "127.0.0.1" : options.Bind;
Console.WriteLine($"[weavie-runner] workspace: {options.WorkspaceRoot}");
Console.WriteLine($"[weavie-runner] worker headless: {options.HeadlessPath} (port {backend.Port})");
Console.WriteLine($"[weavie-runner] control plane: http://{shown}:{options.Port}/?token={options.RunnerToken}");
Console.WriteLine($"[weavie-runner] runner token: {options.RunnerToken}");
Console.Out.Flush();

await app.RunAsync().ConfigureAwait(false);
return 0;

static void Log(string line) {
	Console.WriteLine(line);
	Console.Out.Flush();
}
