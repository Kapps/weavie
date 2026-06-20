using Weavie.Runner;

var options = RunnerOptions.Resolve(args);

var sessions = await SessionManager.CreateAsync(options, Log).ConfigureAwait(false);
if (sessions is null) {
	Console.Error.WriteLine(
		$"[weavie-runner] {options.WorkspaceRoot} is not a git repository — worktree-backed sessions need git. "
		+ "Point --workspace at a repo.");
	return 1;
}

if (!File.Exists(options.HeadlessPath)
	&& !options.HeadlessPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
	&& Path.GetDirectoryName(options.HeadlessPath) is { Length: > 0 }) {
	Console.WriteLine($"[weavie-runner] warning: headless binary not found at {options.HeadlessPath}; pass --headless <path>.");
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://{options.Bind}:{options.Port}");
var app = builder.Build();

ControlApi.Map(app, sessions, options);

string shown = options.Bind is "0.0.0.0" or "::" ? "127.0.0.1" : options.Bind;
Console.WriteLine($"[weavie-runner] workspace: {options.WorkspaceRoot}");
Console.WriteLine($"[weavie-runner] worker headless: {options.HeadlessPath}");
Console.WriteLine($"[weavie-runner] control plane: http://{shown}:{options.Port}/?token={options.RunnerToken}");
Console.WriteLine($"[weavie-runner] runner token: {options.RunnerToken}");
Console.Out.Flush();

await using (sessions) {
	await app.RunAsync().ConfigureAwait(false);
}

return 0;

static void Log(string line) {
	Console.WriteLine(line);
	Console.Out.Flush();
}
