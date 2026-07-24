using System.Runtime.InteropServices;
using Weavie.Core.Mcp;
using Weavie.Headless;
using Weavie.Hosting;
using Weavie.Hosting.Web;

string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
int port = ResolvePort(args);
string workspaceOverride = ResolveWorkspace(args);
var (listen, listenError) = ListenMode.Resolve(args);
if (listen is null) {
	Console.Error.WriteLine($"[weavie-headless] {listenError}");
	return 1;
}

var dispatcher = new SerialUiDispatcher(ex => {
	Console.Error.WriteLine($"[weavie-headless] dispatched action failed: {ex}");
	Console.Error.Flush();
	Environment.FailFast("weavie-headless: a dispatched UI action threw", ex);
});
var bridge = new WebSocketHostBridge(dispatcher);
var services = HostServices.CreateDefault();
if (Environment.GetEnvironmentVariable("WEAVIE_FAKE_PRS") is { Length: > 0 } fakePrsPath && File.Exists(fakePrsPath)) {
	var fakePrs = FakePullRequests.FromFile(fakePrsPath);
	services = services with { PullRequests = fakePrs, ReviewComments = fakePrs };
}

if (Environment.GetEnvironmentVariable("WEAVIE_FAKE_NOTION") is { Length: > 0 } fakeNotionPath && File.Exists(fakeNotionPath)) {
	services = services with { Sources = FakeNotionSource.FromFile(fakeNotionPath) };
}

string workspace = !string.IsNullOrEmpty(workspaceOverride)
	? workspaceOverride
	: services.Settings.GetString("workspace") ?? Environment.CurrentDirectory;
var http = listen is ListenMode.Remote remote
	? new WorkspaceHttpServerOptions(remote.Bind, port, remote.Token, wwwroot, true)
	: WorkspaceHttpServerOptions.Loopback(port, wwwroot);
var transport = listen is ListenMode.Remote ? HostTransport.Remote : HostTransport.Local;
await using var core = new HostCore(
	new HeadlessPlatform(bridge, dispatcher, transport),
	services,
	workspace,
	http,
	bridge);

await core.StartAsync().ConfigureAwait(false);
Console.WriteLine($"[weavie-headless] workspace: {core.WorkspaceRoot}");
Console.WriteLine($"[weavie-headless] open  {core.WorkspacePageUrl}  in a browser");
Console.Out.Flush();
using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, args) => {
	args.Cancel = true;
	shutdown.Cancel();
};
Console.CancelKeyPress += cancel;
using var termination = OperatingSystem.IsWindows()
	? null
	: PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => {
		context.Cancel = true;
		shutdown.Cancel();
	});
try {
	await core.WaitForShutdownAsync().WaitAsync(shutdown.Token).ConfigureAwait(false);
} catch (OperationCanceledException) when (shutdown.IsCancellationRequested) {
	// The process signal enters the same orderly HostCore disposal as an HTTP drain.
} finally {
	Console.CancelKeyPress -= cancel;
}

services.Dispose();
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
