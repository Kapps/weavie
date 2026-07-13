namespace Weavie.Runner;

/// <summary>
/// Decides how the runner's endpoints are reached over TLS. An implementation owns the bind addresses the
/// control plane and worker listen on, builds the <c>https://</c> worker URL <c>/backend</c> advertises (from
/// which the page derives <c>wss://</c>), and — for an active terminator like <c>tailscale serve</c> — sets up
/// and tears the termination down. See docs/specs/tls-on-the-runner.md.
/// </summary>
internal interface ITlsFront : IAsyncDisposable {
	/// <summary>The interface the control-plane Kestrel server binds.</summary>
	string ControlBindAddress { get; }

	/// <summary>The interface a spawned worker headless binds.</summary>
	string WorkerBindAddress { get; }

	/// <summary>
	/// The control-plane URL an operator registers as the remote agent (shown at startup), given the
	/// <paramref name="controlPort"/> Kestrel actually bound (meaningful when launched with --port 0).
	/// </summary>
	string RegisterUrl(int controlPort);

	/// <summary>
	/// The worker page URL <c>/backend</c> returns for <paramref name="backend"/>, reached from a client that
	/// hit the control plane at <paramref name="requestHost"/>.
	/// </summary>
	string WorkerPageUrl(string requestHost, WorkspaceBackend backend);
}
