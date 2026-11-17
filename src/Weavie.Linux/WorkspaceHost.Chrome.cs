using System.Runtime.InteropServices;
using Weavie.Core.Shell;
using Weavie.Linux.Hosting;
using Weavie.Linux.Native;

namespace Weavie.Linux;

// The window's own title bar: client-side decorations with no visible GTK header, so the page's bar is the only
// one. The page draws the desktop's button layout and reports caption presses; the press is paired with the native
// one recorded here and run as the desktop's title-bar gesture.
internal sealed partial class WorkspaceHost {
	private readonly CaptionGesture _caption = new();
	private IntPtr _lastPress;
	private double _pressX;
	private double _pressY;
	private WindowChrome _chrome = WindowChrome.NativeFrame;
	private Action? _chromeChanged;
	private LegacyEventCallback? _onPointerEvent;
	private PropertyNotifyCallback? _onLayoutChanged;

	WindowChrome IShellWindow.Chrome => _chrome;

	event Action? IShellWindow.ChromeChanged {
		add => _chromeChanged += value;
		remove => _chromeChanged -= value;
	}

	void IShellWindow.Minimize() => Gtk.gtk_window_minimize(_window);

	void IShellWindow.ToggleMaximize() => ToggleMaximize();

	void IShellWindow.Close() => Gtk.gtk_window_close(_window);

	void IShellWindow.PressCaption(CaptionPress press) {
		switch (_caption.PagePress(press)) {
			case CaptionAction.DoubleClick:
				Perform(Setting("gtk-titlebar-double-click"));
				break;
			case CaptionAction.MiddleClick:
				Perform(Setting("gtk-titlebar-middle-click"));
				break;
			case CaptionAction.SecondaryClick:
				Perform(Setting("gtk-titlebar-right-click"));
				break;
		}
	}

	// Before the window is first presented: GTK chooses client-side decorations when it is realized.
	private void AttachChrome() {
		IntPtr titlebar = Gtk.gtk_box_new(Gtk.OrientationHorizontal, 0);
		Gtk.gtk_widget_set_visible(titlebar, false);
		Gtk.gtk_window_set_titlebar(_window, titlebar);

		_onPointerEvent = OnPointerEvent;
		IntPtr controller = Gtk.gtk_event_controller_legacy_new();
		Gtk.gtk_event_controller_set_propagation_phase(controller, Gtk.PhaseCapture);
		_ = GLib.g_signal_connect_data(
			controller, "event", Marshal.GetFunctionPointerForDelegate(_onPointerEvent), IntPtr.Zero, IntPtr.Zero, 0);
		Gtk.gtk_widget_add_controller(_window, controller);

		_onLayoutChanged = (_, _, _) => {
			_chrome = Describe();
			_chromeChanged?.Invoke();
		};
		_ = GLib.g_signal_connect_data(
			Gtk.gtk_settings_get_default(), "notify::gtk-decoration-layout",
			Marshal.GetFunctionPointerForDelegate(_onLayoutChanged), IntPtr.Zero, IntPtr.Zero, 0);
		_chrome = Describe();
	}

	private static WindowChrome Describe() => new(
		true, CaptionMode.Press, false, null, DecorationLayout.Parse(Setting("gtk-decoration-layout")), default);

	private static string? Setting(string name) => GLib.GetStringProperty(Gtk.gtk_settings_get_default(), name);

	// Records presses and drives a pending move; never consumes, so WebKit sees every event too.
	private int OnPointerEvent(IntPtr controller, IntPtr gdkEvent, IntPtr userData) {
		switch (Gdk.gdk_event_get_event_type(gdkEvent)) {
			case Gdk.ButtonPress:
				if (_lastPress != IntPtr.Zero) {
					Gdk.gdk_event_unref(_lastPress);
				}

				_lastPress = Gdk.gdk_event_ref(gdkEvent);
				_ = Gdk.gdk_event_get_position(gdkEvent, out _pressX, out _pressY);
				_caption.NativePress(Gdk.gdk_button_event_get_button(gdkEvent));
				break;
			case Gdk.ButtonRelease:
				_caption.NativeRelease();
				break;
			case Gdk.MotionNotify:
				_ = Gdk.gdk_event_get_position(gdkEvent, out double x, out double y);
				bool past = Gtk.gtk_drag_check_threshold(_window, (int)_pressX, (int)_pressY, (int)x, (int)y);
				if (_caption.NativeMotion(past) == CaptionAction.Move) {
					Gdk.gdk_toplevel_begin_move(
						Surface(), Gdk.gdk_event_get_device(_lastPress), 1, _pressX, _pressY, Gdk.gdk_event_get_time(gdkEvent));
				}

				break;
		}

		return 0;
	}

	// The actions GTK's own title bar runs for the desktop's gtk-titlebar-* settings; anything else does nothing.
	private void Perform(string? action) {
		switch (action) {
			case "toggle-maximize":
				ToggleMaximize();
				break;
			case "minimize":
				Gtk.gtk_window_minimize(_window);
				break;
			case "lower":
				_ = Gdk.gdk_toplevel_lower(Surface());
				break;
			case "menu":
				_ = Gdk.gdk_toplevel_show_window_menu(Surface(), _lastPress);
				break;
		}
	}

	private void ToggleMaximize() {
		if (Gtk.gtk_window_is_maximized(_window)) {
			Gtk.gtk_window_unmaximize(_window);
		} else {
			Gtk.gtk_window_maximize(_window);
		}
	}

	private IntPtr Surface() => Gtk.gtk_native_get_surface(_window);
}
