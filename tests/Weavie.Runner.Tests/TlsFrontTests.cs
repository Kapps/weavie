using Xunit;

namespace Weavie.Runner.Tests;

/// <summary>
/// Covers the <see cref="ITlsFront"/> implementations: <see cref="StaticFront"/>'s URL building for the local
/// and proxy modes, and <see cref="TailscaleServeFront"/>'s serve setup, advertised MagicDNS URL, teardown, and
/// loud failures — all driven by a fake <see cref="ITailscaleCli"/>, so no real daemon is touched.
/// </summary>
public sealed class TlsFrontTests {
	private static RunnerOptions Options(TlsMode mode) => new() {
		WorkspaceRoot = "/repo",
		HeadlessPath = "/headless.dll",
		RunnerToken = "rtok",
		Tls = mode,
	};

	private static WorkspaceBackend Backend(int port, string token) => new() {
		WorkspaceRoot = "/repo",
		Port = port,
		Token = token,
	};

	[Fact]
	public void Static_none_builds_loopback_http_url() {
		var front = new StaticFront(Options(TlsMode.None));
		Assert.Equal("127.0.0.1", front.WorkerBindAddress);
		Assert.Equal("http://boxhost:5555/?token=tok", front.WorkerPageUrl("boxhost", Backend(5555, "tok")));
	}

	[Fact]
	public void Static_proxy_builds_https_url_from_public_host() {
		var options = Options(TlsMode.Proxy) with { PublicHost = "weavie.example.com", WorkerHttpsPort = 8443 };
		var front = new StaticFront(options);
		Assert.Equal("127.0.0.1", front.WorkerBindAddress);
		Assert.Equal("https://weavie.example.com:8443/?token=tok", front.WorkerPageUrl("ignored", Backend(7000, "tok")));
	}

	[Fact]
	public void Tailscale_maps_both_ports_and_advertises_magicdns() {
		var cli = new FakeTailscaleCli();
		var options = Options(TlsMode.Tailscale) with { Port = 8800, WorkerPort = 8701, WorkerHttpsPort = 8443, ControlHttpsPort = 443 };
		var front = new TailscaleServeFront(options, cli, log: null);

		Assert.Contains(cli.Calls, c => c.SequenceEqual(["serve", "--bg", "--https=443", "http://127.0.0.1:8800"]));
		Assert.Contains(cli.Calls, c => c.SequenceEqual(["serve", "--bg", "--https=8443", "http://127.0.0.1:8701"]));
		Assert.Equal("https://box.tnet.ts.net", front.RegisterUrl); // 443 omitted
		Assert.Equal("https://box.tnet.ts.net:8443/?token=tok", front.WorkerPageUrl("ignored", Backend(1, "tok")));
	}

	[Fact]
	public async Task Tailscale_clears_mappings_on_dispose() {
		var cli = new FakeTailscaleCli();
		var front = new TailscaleServeFront(Options(TlsMode.Tailscale) with { WorkerPort = 8701 }, cli, log: null);
		await front.DisposeAsync();
		Assert.Contains(cli.Calls, c => c.SequenceEqual(["serve", "--https=443", "off"]));
		Assert.Contains(cli.Calls, c => c.SequenceEqual(["serve", "--https=8443", "off"]));
	}

	[Fact]
	public void Tailscale_fails_loud_when_logged_out() {
		var cli = new FakeTailscaleCli { StatusResult = new TailscaleResult(0, "{\"Self\":{}}", "") };
		var ex = Assert.Throws<InvalidOperationException>(
			() => new TailscaleServeFront(Options(TlsMode.Tailscale) with { WorkerPort = 8701 }, cli, log: null));
		Assert.Contains("MagicDNS", ex.Message);
	}

	[Fact]
	public void Tailscale_fails_loud_when_serve_fails() {
		var cli = new FakeTailscaleCli { ServeResult = new TailscaleResult(1, "", "HTTPS not enabled for this tailnet") };
		var ex = Assert.Throws<InvalidOperationException>(
			() => new TailscaleServeFront(Options(TlsMode.Tailscale) with { WorkerPort = 8701 }, cli, log: null));
		Assert.Contains("serve", ex.Message);
	}

	private sealed class FakeTailscaleCli : ITailscaleCli {
		public List<string[]> Calls { get; } = [];

		public TailscaleResult StatusResult { get; set; } = new(0, "{\"Self\":{\"DNSName\":\"box.tnet.ts.net.\"}}", "");

		public TailscaleResult ServeResult { get; set; } = new(0, "", "");

		public TailscaleResult Run(IReadOnlyList<string> args) {
			Calls.Add([.. args]);
			return args[0] == "status" ? StatusResult : ServeResult;
		}
	}
}
