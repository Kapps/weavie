using System.Text;
using System.Text.Json;

namespace Weavie.Hosting.Web;

/// <summary>
/// The shared welcome-screen flow: inject the recents the page reads (<c>window.__WEAVIE_WELCOME__</c>), navigate to
/// welcome.html, and route its <c>menu-action</c> messages (Open Folder / Open Recent) to the host's open handlers.
/// Every host drives the one welcome UI through this — supplying only the native <see cref="IWebSurface"/> +
/// <see cref="IHostBridge"/>, the welcome URL, the live recents, and the two open handlers — so the protocol,
/// the recents JSON, and the refresh live in one place instead of being re-implemented per OS.
/// </summary>
public sealed class WelcomeController {
	private readonly IHostBridge _bridge;
	private readonly IWebSurface _surface;
	private readonly string _welcomeUrl;
	private readonly Func<IReadOnlyList<string>> _recents;
	private readonly Action _onOpenFolder;
	private readonly Action<string> _onOpenRecent;
	private Action<string>? _onMessage;

	/// <param name="bridge">The host's web-message bridge (the welcome page's <c>menu-action</c> messages arrive here).</param>
	/// <param name="surface">The host's native WebView ops (inject + navigate).</param>
	/// <param name="welcomeUrl">The welcome page URL for this host (e.g. <c>app://app/welcome.html</c>).</param>
	/// <param name="recents">The current recent-workspace paths, read fresh on each show/refresh.</param>
	/// <param name="onOpenFolder">Invoked for Open Folder: the host shows its native picker and opens the choice.</param>
	/// <param name="onOpenRecent">Invoked for Open Recent with the chosen path: the host opens it (or prunes + <see cref="RefreshAsync"/>).</param>
	public WelcomeController(
		IHostBridge bridge,
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

	/// <summary>Injects the recents, starts routing the page's menu-actions, and navigates to the welcome screen.</summary>
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

	private void OnMessage(string json) {
		if (!TryParseMenuAction(json, out string action, out string? path)) {
			return;
		}

		switch (action) {
			case "open-folder":
				_onOpenFolder();
				break;
			case "open-recent":
				if (!string.IsNullOrEmpty(path)) {
					_onOpenRecent(path);
				}

				break;
		}
	}

	// Parses a `menu-action` web message into (action, path); false for any other message or malformed JSON.
	private static bool TryParseMenuAction(string json, out string action, out string? path) {
		action = string.Empty;
		path = null;
		try {
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object
				|| !root.TryGetProperty("type", out var type)
				|| type.GetString() != "menu-action") {
				return false;
			}

			action = root.TryGetProperty("action", out var a) ? a.GetString() ?? string.Empty : string.Empty;
			path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
			return true;
		} catch (JsonException) {
			return false;
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
