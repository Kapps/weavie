namespace Weavie.Runner;

/// <summary>
/// A front that performs no TLS work itself: it only chooses bind addresses and builds the URLs the control
/// plane advertises. Covers <see cref="TlsMode.None"/> (loopback <c>http</c>, for local/headless use) and
/// <see cref="TlsMode.Proxy"/> (<c>https</c> URLs pointing at an operator-run TLS terminator that fronts the
/// loopback ports). See <see cref="ITlsFront"/>.
/// </summary>
internal sealed class StaticFront : ITlsFront {
	private readonly RunnerOptions _options;

	/// <summary>Creates the front for <paramref name="options"/> (<see cref="TlsMode.None"/> or <see cref="TlsMode.Proxy"/>).</summary>
	public StaticFront(RunnerOptions options) {
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
	}

	private bool Secure => _options.Tls == TlsMode.Proxy;

	/// <inheritdoc/>
	public string ControlBindAddress => Secure ? "127.0.0.1" : _options.Bind;

	/// <inheritdoc/>
	public string WorkerBindAddress => Secure ? "127.0.0.1" : _options.WorkerBind;

	/// <inheritdoc/>
	public string RegisterUrl(int controlPort) => Secure
		? $"https://{TlsUrls.HostPort(_options.PublicHost!, _options.ControlHttpsPort)}"
		: $"http://{_options.Bind}:{controlPort}";

	/// <inheritdoc/>
	public string WorkerPageUrl(string requestHost, WorkspaceBackend backend) {
		ArgumentNullException.ThrowIfNull(backend);
		return Secure
			? $"https://{TlsUrls.HostPort(_options.PublicHost!, _options.WorkerHttpsPort)}/?token={backend.Token}"
			: $"http://{requestHost}:{backend.Port}/?token={backend.Token}";
	}

	/// <inheritdoc/>
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
