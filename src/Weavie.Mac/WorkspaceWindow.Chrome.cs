using Foundation;
using Weavie.Core.Shell;
using Weavie.Mac.Hosting;

namespace Weavie.Mac;

// The window's title bar: the page draws the bar under AppKit's own traffic lights, and a caption press it reports
// becomes the system's drag or double-click action, paired with the press the web view recorded.
internal sealed partial class WorkspaceWindow : IShellWindow {
	private int _barHeight;
	private ChromeInset _inset;
	private bool _fullScreen;
	private Action? _chromeChanged;
	private NSObject? _enterFullScreenObserver;
	private NSObject? _exitFullScreenObserver;

	// In fullscreen the traffic lights leave with the menu bar and the bar keeps its stylesheet height.
	WindowChrome IShellWindow.Chrome => _fullScreen
		? new(false, CaptionMode.Press, false, null, WindowControls.None, default)
		: new(false, CaptionMode.Press, false, _barHeight, WindowControls.None, _inset);

	event Action? IShellWindow.ChromeChanged {
		add => _chromeChanged += value;
		remove => _chromeChanged -= value;
	}

	void IShellWindow.PressCaption(CaptionPress press) {
		switch (_webView.Caption.PagePress(press)) {
			case CaptionAction.Drag:
				Window.PerformWindowDrag(_webView.LastPress!);
				break;
			case CaptionAction.DoubleClick when MacWindowChrome.DoubleClickAction() == MacDoubleClickAction.Zoom:
				Window.Zoom(null);
				break;
			case CaptionAction.DoubleClick when MacWindowChrome.DoubleClickAction() == MacDoubleClickAction.Minimize:
				Window.Miniaturize(null);
				break;
		}
	}

	private void ApplyChrome() {
		MacWindowChrome.Apply(Window);
		(_barHeight, _inset) = MacWindowChrome.Measure(Window);
		_webView.CaptionHeight = _barHeight;
		_enterFullScreenObserver = NSNotificationCenter.DefaultCenter.AddObserver(
			NSWindow.WillEnterFullScreenNotification, _ => SetFullScreen(true), Window);
		_exitFullScreenObserver = NSNotificationCenter.DefaultCenter.AddObserver(
			NSWindow.WillExitFullScreenNotification, _ => SetFullScreen(false), Window);
	}

	private void SetFullScreen(bool fullScreen) {
		_fullScreen = fullScreen;
		_chromeChanged?.Invoke();
	}

	private void DetachChrome() {
		RemoveObserver(ref _enterFullScreenObserver);
		RemoveObserver(ref _exitFullScreenObserver);
	}
}
