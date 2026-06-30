namespace Weavie.Runner;

/// <summary>Builds the <see cref="ITlsFront"/> matching a runner's <see cref="TlsMode"/>.</summary>
internal static class TlsFronts {
	/// <summary>
	/// Creates the front for <paramref name="options"/>. The tailscale front sets up <c>tailscale serve</c> in its
	/// constructor (driving <paramref name="cli"/>), so this can throw — the caller exits the runner on failure.
	/// </summary>
	public static ITlsFront Create(RunnerOptions options, ITailscaleCli cli, Action<string>? log) {
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(cli);
		return options.Tls == TlsMode.Tailscale
			? new TailscaleServeFront(options, cli, log)
			: new StaticFront(options);
	}
}

/// <summary>Small URL helpers shared by the fronts.</summary>
internal static class TlsUrls {
	/// <summary>Joins host and port, omitting the port when it is the https default (443).</summary>
	public static string HostPort(string host, int port) => port == 443 ? host : $"{host}:{port}";
}
