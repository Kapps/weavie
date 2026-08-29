using System.Runtime.InteropServices;
using Weavie.Linux.Native;

namespace Weavie.Linux;

// WebKitGTK consumes Ctrl+Tab focus traversal before DOM keydown. Re-inject both directions into the web
// resolver that owns every effective/user-overridden binding and context guard.
internal sealed partial class WorkspaceHost {
	private const uint IntentModifiers = Gdk.ShiftMask | Gdk.ControlMask | Gdk.AltMask
		| Gdk.SuperMask | Gdk.HyperMask | Gdk.MetaMask;

	private const string ForwardTabKeydown =
		"window.dispatchEvent(new KeyboardEvent('keydown',{key:'Tab',code:'Tab',"
		+ "ctrlKey:true,bubbles:true,cancelable:true}));";
	private const string ReverseTabKeydown =
		"window.dispatchEvent(new KeyboardEvent('keydown',{key:'ISO_Left_Tab',code:'Tab',"
		+ "ctrlKey:true,shiftKey:true,bubbles:true,cancelable:true}));";

	// Capture phase: the window sees the key before the web view hands it to WebKit's focus traversal.
	private void AttachKeyController() {
		_onKeyPress = OnKeyPress;
		IntPtr controller = Gtk.gtk_event_controller_key_new();
		Gtk.gtk_event_controller_set_propagation_phase(controller, Gtk.PhaseCapture);
		_ = GLib.g_signal_connect_data(
			controller, "key-pressed", Marshal.GetFunctionPointerForDelegate(_onKeyPress), IntPtr.Zero, IntPtr.Zero, 0);
		Gtk.gtk_widget_add_controller(_window, controller);
	}

	private int OnKeyPress(IntPtr controller, uint keyval, uint keycode, uint state, IntPtr userData) {
		string? keydown = TabKeydownScript(keyval, state);
		if (keydown is null) {
			return 0;
		}

		WebKit.webkit_web_view_evaluate_javascript(
			_webView, keydown, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
		return 1;
	}

	internal static string? TabKeydownScript(uint keyval, uint state) {
		uint modifiers = state & IntentModifiers;
		if (keyval is not (Gdk.Tab or Gdk.IsoLeftTab)
			|| modifiers is not (Gdk.ControlMask or (Gdk.ControlMask | Gdk.ShiftMask))) {
			return null;
		}

		return modifiers == Gdk.ControlMask ? ForwardTabKeydown : ReverseTabKeydown;
	}
}
