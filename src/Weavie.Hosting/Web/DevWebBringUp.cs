// Debug-only — the dev-server bring-up flow. Release brings the app up directly via WebAppLauncher against the
// bundled origin, so this whole orchestrator is compiled out.
#if DEBUG
namespace Weavie.Hosting.Web;

/// <summary>
/// The shared Debug bring-up: try the Vite dev server and, on success, launch the app against it; on failure
/// render the loud error page (never a silent stale-bundle swap) and let the developer retry or explicitly
/// accept the bundle. One implementation for every desktop host — the host supplies only the native WebView ops
/// (<see cref="IWebSurface"/>) and wires the error page's <c>weavie-dev://</c> links back to
/// <see cref="RunAsync"/> / <see cref="LoadBundleAsync"/>.
/// </summary>
public sealed class DevWebBringUp : IDisposable {
	/// <summary>Link the error page's Retry button navigates to; the host intercepts (and cancels) it.</summary>
	public const string RetryUrl = "weavie-dev://retry";

	/// <summary>Link the error page's "load stale bundle" button navigates to; the host intercepts (and cancels) it.</summary>
	public const string BundleUrl = "weavie-dev://bundle";

	private readonly WebAppLauncher _launcher;
	private readonly IWebSurface _surface;
	private readonly string _bundleOrigin;
	private readonly WebDevServer _devServer;

	/// <param name="launcher">The shared success-path bring-up.</param>
	/// <param name="surface">The host's native WebView operations (for rendering the error page).</param>
	/// <param name="devWebRoot">The Vite source dir (from <see cref="DevWebRoot"/>), or <c>null</c> when unresolved.</param>
	/// <param name="bundleOrigin">The host's bundled-assets origin, used when the developer accepts the stale bundle.</param>
	/// <param name="log">Sink for dev-server output.</param>
	public DevWebBringUp(WebAppLauncher launcher, IWebSurface surface, string? devWebRoot, string bundleOrigin, Action<string> log) {
		ArgumentNullException.ThrowIfNull(launcher);
		ArgumentNullException.ThrowIfNull(surface);
		ArgumentException.ThrowIfNullOrEmpty(bundleOrigin);
		ArgumentNullException.ThrowIfNull(log);
		_launcher = launcher;
		_surface = surface;
		_bundleOrigin = bundleOrigin;
		_devServer = new WebDevServer(log, devWebRoot);
	}

	/// <summary>
	/// Tries to (re)start the dev server. On success, brings the app up against it and returns the dev origin;
	/// on failure, renders the loud error page and returns <c>null</c>. Used for the initial attempt and Retry.
	/// </summary>
	public async Task<string?> RunAsync() {
		string? origin = await _devServer.StartAsync().ConfigureAwait(false);
		if (origin is null) {
			_surface.RenderHtml(DevServerErrorPage.Render(
				_devServer.LastFailure ?? new DevServerFailureInfo("The Vite dev server did not start.", Array.Empty<string>())));
			return null;
		}

		await _launcher.LaunchAsync(origin).ConfigureAwait(false);
		return origin;
	}

	/// <summary>The developer explicitly accepts the possibly-stale bundle: brings the app up against it.</summary>
	public Task LoadBundleAsync() => _launcher.LaunchAsync(_bundleOrigin);

	/// <summary>
	/// (Re)starts the dev server and returns its origin without re-running the backend — for a host recovering a
	/// reload that failed because a reused server went away mid-session (same origin, backend still valid).
	/// </summary>
	public Task<string?> ReviveAsync() => _devServer.StartAsync();

	/// <summary>Kills the Vite process this run spawned (a reused one is left alone).</summary>
	public void Dispose() => _devServer.Dispose();
}
#endif
