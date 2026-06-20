using Weavie.Runner;

var options = RunnerOptions.Resolve(args);

if (!File.Exists(options.HeadlessPath)
	&& !options.HeadlessPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
	&& Path.GetDirectoryName(options.HeadlessPath) is { Length: > 0 }) {
	Console.WriteLine($"[weavie-runner] warning: headless binary not found at {options.HeadlessPath}; pass --headless <path>.");
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
