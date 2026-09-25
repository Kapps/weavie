using System.Text;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Shell;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Web;

/// <summary>
/// The shared welcome-screen flow: inject the recents plus the app-wide page globals, navigate to welcome.html, and
/// serve the page over its own message bus — Open Folder / Open Recent go to the host's open handlers, and the
/// app-wide features (<see cref="GlobalHostFeatures"/>) let Getting Started change settings with no workspace open.
/// Every host drives the one welcome UI through this, supplying only its native surface, bridge, and open handlers.
/// </summary>
public sealed class WelcomeController {
	private readonly IWebTransportHub _bridge;
	private readonly IWebSurface _surface;
	private readonly IUiDispatcher _ui;
	private readonly HostServices _services;
	private readonly string _welcomeUrl;
	private readonly Func<IReadOnlyList<string>> _recents;
	private readonly Action _onOpenFolder;
	private readonly Action<string> _onOpenRecent;
	private Attachment? _attachment;
	private bool _detached;

	/// <param name="bridge">The host's web-message bridge the welcome page talks over.</param>
	/// <param name="surface">The host's native WebView ops (inject + navigate).</param>
	/// <param name="ui">The host's UI-thread dispatcher; the open handlers run on it.</param>
	/// <param name="services">The app-global stores the welcome page reads and changes.</param>
	/// <param name="welcomeUrl">The welcome page URL for this host (e.g. <c>app://app/welcome.html</c>).</param>
	/// <param name="recents">The current recent-workspace paths, read fresh on each show/refresh.</param>
	/// <param name="onOpenFolder">Invoked for Open Folder: the host shows its native picker and opens the choice.</param>
	/// <param name="onOpenRecent">Invoked for Open Recent with the chosen path: the host opens it (or prunes + <see cref="RefreshAsync"/>).</param>
	public WelcomeController(
		IWebTransportHub bridge,
		IWebSurface surface,
		IUiDispatcher ui,
		HostServices services,
		string welcomeUrl,
		Func<IReadOnlyList<string>> recents,
		Action onOpenFolder,
		Action<string> onOpenRecent) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(surface);
		ArgumentNullException.ThrowIfNull(ui);
		ArgumentNullException.ThrowIfNull(services);
		ArgumentException.ThrowIfNullOrEmpty(welcomeUrl);
		ArgumentNullException.ThrowIfNull(recents);
		ArgumentNullException.ThrowIfNull(onOpenFolder);
		ArgumentNullException.ThrowIfNull(onOpenRecent);
		_bridge = bridge;
		_surface = surface;
		_ui = ui;
		_services = services;
		_welcomeUrl = welcomeUrl;
		_recents = recents;
		_onOpenFolder = onOpenFolder;
		_onOpenRecent = onOpenRecent;
	}

	/// <summary>Starts serving the welcome page's messages, injects the bootstrap, and navigates to it.</summary>
	public async Task ShowAsync() {
		await _ui.InvokeAsync(() => {
			if (!_detached) {
				_attachment ??= new Attachment(this);
			}

			return RefreshAsync();
		}, CancellationToken.None).ConfigureAwait(false);

		// A Finder / desktop-entry launch has a truncated PATH; once the login shell's is imported, re-push the
		// agents so Getting Started sees installed ones (e.g. claude) the way a terminal would.
		string failure = await LoginShellEnvironment.ImportOnceAsync(Log).ConfigureAwait(false);
		if (failure.Length > 0) {
			Log($"[welcome] {failure}");
		}

		await _ui.InvokeAsync(() => {
			_attachment?.Global.PushAgentDefaults();
			return Task.CompletedTask;
		}, CancellationToken.None).ConfigureAwait(false);
	}

	/// <summary>Re-injects current bootstrap state and reloads the welcome screen; a no-op once detached. UI thread.</summary>
	public Task RefreshAsync() {
		if (_attachment is not { } attachment) {
			return Task.CompletedTask;
		}

		return _surface.LoadAsync(
			_welcomeUrl,
			$"window.__WEAVIE_WELCOME__ = {BuildConfigJson(_recents(), _services.Settings.RequireBool(CoreSettings.GettingStartedCompleted))};"
			+ attachment.Global.BootstrapScript());
	}

	/// <summary>Stops serving the welcome page — call when leaving the welcome surface for a workspace.</summary>
	public void Detach() {
		_detached = true;
		var attachment = _attachment;
		_attachment = null;
		attachment?.Close();
	}

	private static void Log(string line) {
		Console.WriteLine(line);
		Console.Out.Flush();
	}

	// The window.__WEAVIE_WELCOME__ payload, hand-built so it stays trim-safe on every host (JsonSerializer of an
	// anonymous type is IL2026 on the macOS SDK).
	private static string BuildConfigJson(IReadOnlyList<string> recents, bool setupCompleted) {
		var sb = new StringBuilder("{\"recents\":[");
		for (int i = 0; i < recents.Count; i++) {
			if (i > 0) {
				sb.Append(',');
			}

			sb.Append('"').Append(JsonEncodedText.Encode(recents[i]).ToString()).Append('"');
		}

		return sb.Append("],\"setupCompleted\":").Append(setupCompleted ? "true" : "false").Append('}').ToString();
	}

	// One welcome page's message plumbing: the router + ingress over the shared bridge, the app-wide features, and
	// the menu handler. Closed as a unit when the window leaves the welcome screen.
	private sealed class Attachment {
		private readonly WelcomeController _owner;
		private readonly HostMessageRouter _router;
		private readonly MessageIngress _ingress;

		public Attachment(WelcomeController owner) {
			_owner = owner;
			_router = new HostMessageRouter(owner._bridge, owner._ui, Log);
			_ingress = new MessageIngress(owner._ui, _router.RouteAsync, _router.Disconnect, _router.Diagnostics);
			Global = new GlobalHostFeatures(_router.Host, owner._services, Log);
			_router.Host.Feature("window").HandleAfterEvent<JsonElement>("menu", (message, _) =>
				Task.FromResult<Func<CancellationToken, Task>>(ct => owner._ui.InvokeAsync(() => {
					OnMenu(message);
					return Task.CompletedTask;
				}, ct)));
			owner._bridge.MessageReceived += _ingress.Enqueue;
			owner._bridge.PeerDisconnected += _ingress.EnqueueDisconnect;
		}

		public GlobalHostFeatures Global { get; }

		// Unhooks synchronously so a workspace can take over the bridge at once; the queues drain in the background
		// because Close usually runs inside this router's own menu handler.
		public void Close() {
			_owner._bridge.MessageReceived -= _ingress.Enqueue;
			_owner._bridge.PeerDisconnected -= _ingress.EnqueueDisconnect;
			Global.Dispose();
			_ = DisposeAsync();
		}

		private async Task DisposeAsync() {
			try {
				await _ingress.DisposeAsync().ConfigureAwait(false);
				await _router.DisposeAsync().ConfigureAwait(false);
			} catch (Exception ex) {
				Log($"[welcome] message teardown failed: {ex}");
			}
		}

		private void OnMenu(JsonElement message) {
			if (!ShellProtocol.TryParseMenuAction(message, out var command, out string? path)) {
				return;
			}

			switch (command) {
				case MenuCommand.OpenFolder:
					_owner._onOpenFolder();
					break;
				case MenuCommand.OpenRecent when !string.IsNullOrEmpty(path):
					_owner._onOpenRecent(path);
					break;
			}
		}
	}
}
