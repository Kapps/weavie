using Xunit;

namespace Weavie.Runner.Tests;

/// <summary>
/// Covers <see cref="RunnerOptions.Resolve"/>: the <c>--tls</c> modes, the fail-closed rejection of an exposed
/// bind without TLS, and the worker-port pinning secured modes rely on.
/// </summary>
public sealed class RunnerOptionsTests {
	private static string[] Args(params string[] extra) =>
		["--headless", TempFile(), "--token", "tok", .. extra];

	private static string TempFile() => Path.GetTempFileName();

	[Fact]
	public void Defaults_to_none_on_loopback() {
		var (options, error) = RunnerOptions.Resolve(Args());
		Assert.Null(error);
		Assert.NotNull(options);
		Assert.Equal(TlsMode.None, options.Tls);
		Assert.Equal("127.0.0.1", options.Bind);
		Assert.Null(options.WorkerPort); // a free port is allocated per worker in local mode
	}

	[Fact]
	public void None_with_exposed_bind_fails_closed() {
		var (options, error) = RunnerOptions.Resolve(Args("--bind", "0.0.0.0"));
		Assert.Null(options);
		Assert.NotNull(error);
		Assert.Contains("without TLS", error);
	}

	[Fact]
	public void None_with_exposed_worker_bind_fails_closed() {
		var (options, error) = RunnerOptions.Resolve(Args("--worker-bind", "0.0.0.0"));
		Assert.Null(options);
		Assert.NotNull(error);
		Assert.Contains("worker", error);
	}

	[Fact]
	public void Proxy_requires_public_host() {
		var (options, error) = RunnerOptions.Resolve(Args("--tls", "proxy"));
		Assert.Null(options);
		Assert.NotNull(error);
		Assert.Contains("--public-host", error);
	}

	[Fact]
	public void Proxy_with_public_host_pins_worker_port() {
		var (options, error) = RunnerOptions.Resolve(Args("--tls", "proxy", "--public-host", "weavie.example.com"));
		Assert.Null(error);
		Assert.NotNull(options);
		Assert.Equal(TlsMode.Proxy, options.Tls);
		Assert.Equal("weavie.example.com", options.PublicHost);
		Assert.Equal(8701, options.WorkerPort);
	}

	[Fact]
	public void Tailscale_pins_a_default_worker_port() {
		var (options, error) = RunnerOptions.Resolve(Args("--tls", "tailscale"));
		Assert.Null(error);
		Assert.NotNull(options);
		Assert.Equal(TlsMode.Tailscale, options.Tls);
		Assert.Equal(8701, options.WorkerPort);
	}

	[Fact]
	public void Explicit_worker_port_wins() {
		var (options, error) = RunnerOptions.Resolve(Args("--tls", "tailscale", "--worker-port", "9001"));
		Assert.Null(error);
		Assert.NotNull(options);
		Assert.Equal(9001, options.WorkerPort);
	}

	[Fact]
	public void Unknown_tls_mode_is_rejected() {
		var (options, error) = RunnerOptions.Resolve(Args("--tls", "wireguard"));
		Assert.Null(options);
		Assert.NotNull(error);
		Assert.Contains("not a known mode", error);
	}

	[Fact]
	public void Unparseable_explicit_port_is_rejected() {
		var (options, error) = RunnerOptions.Resolve(Args("--worker-https-port", "not-a-number"));
		Assert.Null(options);
		Assert.NotNull(error);
		Assert.Contains("not a valid port", error);
	}
}
