namespace Weavie.Hosting.Web;

/// <summary>
/// The shared "origin settled, bring the app up" flow: build the live backend for that origin, inject the
/// bootstrap globals so the page mounts at the user's settings with no flash, then navigate. The host supplies
/// only the native WebView ops (<see cref="IWebSurface"/>). Used in Release directly and via <c>DevWebBringUp</c> in Debug.
/// </summary>
public sealed class WebAppLauncher {
	private readonly IWebSurface _surface;
	private readonly HostCore _core;
	private readonly string _indexQuery;

	/// <param name="surface">The host's native WebView operations.</param>
	/// <param name="core">The shared core whose backend + bootstrap this brings up.</param>
	/// <param name="indexQuery">Query string appended to <c>/index.html</c> (e.g. <c>?startuptiming=1</c>), or empty.</param>
	public WebAppLauncher(IWebSurface surface, HostCore core, string indexQuery) {
		ArgumentNullException.ThrowIfNull(surface);
		ArgumentNullException.ThrowIfNull(core);
		ArgumentNullException.ThrowIfNull(indexQuery);
		_surface = surface;
		_core = core;
		_indexQuery = indexQuery;
	}

	/// <summary>Brings the app up against <paramref name="origin"/>: backend, bootstrap injection, then navigation.</summary>
	public async Task LaunchAsync(string origin) {
		ArgumentException.ThrowIfNullOrEmpty(origin);

		// No ConfigureAwait juggling: the surface marshals each call to its own UI thread, so this flow is
		// thread-agnostic and the awaits can stay off the captured context.
		await _core.StartAsync().ConfigureAwait(false);
		await _surface.InjectStartupScriptAsync(_core.BuildBootstrap()).ConfigureAwait(false);
		_surface.Navigate($"{origin}/index.html{_indexQuery}");
	}
}
