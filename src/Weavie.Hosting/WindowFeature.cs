using Weavie.Core.Shell;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

/// <summary>
/// The page side of one native window: routes the title bar's <c>window.control</c>, <c>window.resize</c> and
/// <c>window.captionPress</c> events to it and publishes its chrome description and state. Shared by
/// the workspace core and the welcome screen, so the handlers exist once.
/// </summary>
internal sealed class WindowFeature : IDisposable {
	private readonly MessageFeatureChannel _feature;
	private readonly IShellWindow _window;
	private readonly IDisposable[] _handlers;

	public WindowFeature(HostMessageBus host, IShellWindow window) {
		ArgumentNullException.ThrowIfNull(host);
		ArgumentNullException.ThrowIfNull(window);
		_feature = host.Feature("window");
		_window = window;
		_handlers = [
			_feature.Handle("control", MessageActivity.ActionFailed("Controlling the window"), WireJson.Default.WindowControlRequest, (message, _) => {
				Control(message.Action);
				return Task.CompletedTask;
			}),
			_feature.Handle("resize", MessageActivity.ActionFailed("Resizing the window"), WireJson.Default.WindowResizeRequest, (message, _) => {
				_window.StartResize(message.Edge);
				return Task.CompletedTask;
			}),
			_feature.Handle("captionPress", MessageActivity.Droppable("Moving the window"), WireJson.Default.CaptionPress, (press, _) => {
				_window.PressCaption(press);
				return Task.CompletedTask;
			}),
		];
		_window.ChromeChanged += PushChrome;
	}

	/// <summary>Publishes the native window's current state for title-bar chrome and attention delivery.</summary>
	public void PushState(bool maximized, bool focused) =>
		_feature.Publish("state", WireJson.Default.WindowStateChanged, new(maximized, focused));

	// The boot config carries the chrome a page starts with; only changes are pushed.
	private void PushChrome() => _feature.Publish("chrome", WireJson.Default.WindowChrome, _window.Chrome);

	public void Dispose() {
		_window.ChromeChanged -= PushChrome;
		foreach (var handler in _handlers) {
			handler.Dispose();
		}
	}

	private void Control(WindowControl action) {
		switch (action) {
			case WindowControl.Minimize:
				_window.Minimize();
				break;
			case WindowControl.MaximizeToggle:
				_window.ToggleMaximize();
				break;
			case WindowControl.Close:
				_window.Close();
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown window control.");
		}
	}
}

/// <summary>A <c>window.control</c> event: the title-bar button the user clicked.</summary>
internal sealed record WindowControlRequest(WindowControl Action);

/// <summary>A <c>window.resize</c> event: the frameless-window edge the user grabbed.</summary>
internal sealed record WindowResizeRequest(ResizeEdge Edge);
