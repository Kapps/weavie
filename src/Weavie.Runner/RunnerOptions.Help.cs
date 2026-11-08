namespace Weavie.Runner;

public sealed partial record RunnerOptions {
	internal const string HelpText = """
		Usage: Weavie.Runner [options]

		  --workspace <path>             Repository root (default: current directory)
		  --headless <path>              Worker executable (Weavie.Headless / Weavie.Headless.exe)
		                                 or framework-dependent Weavie.Headless.dll, run through dotnet.
		                                 Overrides WEAVIE_HEADLESS_PATH; otherwise discovers the worker.
		  --bind <address>               Control-plane bind address (default: 127.0.0.1)
		  --port <port>                  Control-plane port (default: 8800; 0 allocates a port)
		  --worker-bind <address>        Worker bind address (default: --bind)
		  --worker-port <port>           Worker port (default: allocated, or 8701 with TLS)
		  --tls <none|proxy|tailscale>    TLS mode (default: none, loopback only)
		  --public-host <host>           Public hostname (required for proxy TLS)
		  --control-https-port <port>    Public control-plane HTTPS port (default: 443)
		  --worker-https-port <port>     Public worker HTTPS port (default: 8443)
		  --token <token>               Control-plane bearer token (default: generated)
		  --auto-update [stable|latest]  Enable worker updates (default channel: stable)
		  --github-token <token>        GitHub token for update downloads
		  --help                        Show this help
		""";
}
