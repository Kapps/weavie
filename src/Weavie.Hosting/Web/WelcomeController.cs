using System.Text;
using System.Text.Json;
using Weavie.Core.Shell;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Web;

/// <summary>
/// The shared welcome-screen flow: inject the recents the page reads (<c>window.__WEAVIE_WELCOME__</c>), navigate to
/// welcome.html, and route its <c>window.menu</c> events (Open Folder / Open Recent) to the host's open handlers.
/// Every host drives the one welcome UI through this — supplying only the native <see cref="IWebSurface"/> +
/// <see cref="IWebTransportHub"/>, the welcome URL, the live recents, and the two open handlers — so the protocol,
/// the recents JSON, and the refresh live in one place instead of being re-implemented per OS.
/// </summary>
public sealed class WelcomeController {
	private readonly IWebTransportHub _bridge;
	private readonly IWebSurface _surface;
	private readonly string _welcomeUrl;
	private readonly Func<IReadOnlyList<string>> _recents;
	private readonly Action _onOpenFolder;
	private readonly Action<string> _onOpenRecent;
	private Action<WebPeer, string>? _onMessage;

	/// <param name="bridge">The host's web-message bridge (the welcome page's <c>window.menu</c> events arrive here).</param>
	/// <param name="surface">The host's native WebView ops (inject + navigate).</param>
	/// <param name="welcomeUrl">The welcome page URL for this host (e.g. <c>app://app/welcome.html</c>).</param>
	/// <param name="recents">The current recent-workspace paths, read fresh on each show/refresh.</param>
	/// <param name="onOpenFolder">Invoked for Open Folder: the host shows its native picker and opens the choice.</param>
	/// <param name="onOpenRecent">Invoked for Open Recent with the chosen path: the host opens it (or prunes + <see cref="RefreshAsync"/>).</param>
	public WelcomeController(
		IWebTransportHub bridge,
		IWebSurface surface,
		string welcomeUrl,
		Func<IReadOnlyList<string>> recents,
		Action onOpenFolder,
		Action<string> onOpenRecent) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(surface);
		ArgumentException.ThrowIfNullOrEmpty(welcomeUrl);
		ArgumentNullException.ThrowIfNull(recents);
		ArgumentNullException.ThrowIfNull(onOpenFolder);
		ArgumentNullException.ThrowIfNull(onOpenRecent);
		_bridge = bridge;
		_surface = surface;
		_welcomeUrl = welcomeUrl;
		_recents = recents;
		_onOpenFolder = onOpenFolder;
		_onOpenRecent = onOpenRecent;
	}

	/// <summary>Injects the recents, starts routing the page's menu events, and navigates to the welcome screen.</summary>
	public async Task ShowAsync() {
		await InjectRecentsAsync().ConfigureAwait(false);
		_onMessage = OnMessage;
		_bridge.MessageReceived += _onMessage;
		_surface.Navigate(_welcomeUrl);
	}

	/// <summary>Re-injects the current recents and reloads the welcome screen (e.g. after pruning a missing folder).</summary>
	public async Task RefreshAsync() {
		await InjectRecentsAsync().ConfigureAwait(false);
		_surface.Navigate(_welcomeUrl);
	}

	/// <summary>Stops routing the welcome page's menu-actions — call when leaving the welcome surface for a workspace.</summary>
	public void Detach() {
		if (_onMessage is not null) {
			_bridge.MessageReceived -= _onMessage;
			_onMessage = null;
		}
	}

	private Task InjectRecentsAsync() =>
		_surface.InjectStartupScriptAsync($"window.__WEAVIE_WELCOME__ = {BuildConfigJson(_recents())};");

	private void OnMessage(WebPeer _, string json) {
		if (!MessageEnvelope.TryParse(json, out var envelope)
			|| envelope is not {
				Scope: MessageScope.Host,
				Kind: MessageKind.Event,
				Feature: "window",
				Name: "menu",
			}
			|| !ShellProtocol.TryParseMenuAction(envelope.Payload, out var command, out string? path)) {
			return;
		}

		switch (command) {
			case MenuCommand.OpenFolder:
				_onOpenFolder();
				break;
			case MenuCommand.OpenRecent:
				if (!string.IsNullOrEmpty(path)) {
					_onOpenRecent(path);
				}

				break;
		}
	}

	// The window.__WEAVIE_WELCOME__ payload, hand-built so it stays trim-safe on every host (JsonSerializer of an
	// anonymous type is IL2026 on the macOS SDK).
	private static string BuildConfigJson(IReadOnlyList<string> recents) {
		var sb = new StringBuilder("{\"recents\":[");
		for (int i = 0; i < recents.Count; i++) {
			if (i > 0) {
				sb.Append(',');
			}

			sb.Append('"').Append(JsonEncodedText.Encode(recents[i]).ToString()).Append('"');
		}

		return sb.Append("]}").ToString();
	}
}
