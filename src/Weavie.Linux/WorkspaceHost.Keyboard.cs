using Weavie.Linux.Native;

namespace Weavie.Linux;

// WebKitGTK consumes Ctrl+Shift+Tab as reverse focus traversal before DOM keydown. Re-inject it into the same
// web resolver that owns every effective/user-overridden binding and context guard.
internal sealed partial class WorkspaceHost {
	private const uint IntentModifiers = Gdk.ShiftMask | Gdk.ControlMask | Gdk.AltMask
		| Gdk.SuperMask | Gdk.HyperMask | Gdk.MetaMask;

	private const string ReverseTabKeydown =
		"window.dispatchEvent(new KeyboardEvent('keydown',{key:'ISO_Left_Tab',code:'Tab',"
		+ "ctrlKey:true,shiftKey:true,bubbles:true,cancelable:true}));";

	private int OnKeyPress(IntPtr widget, IntPtr keyEvent, IntPtr userData) {
		if (!Gdk.gdk_event_get_state(keyEvent, out uint state)
			|| !Gdk.gdk_event_get_keyval(keyEvent, out uint keyval)
			|| (state & IntentModifiers) != (Gdk.ControlMask | Gdk.ShiftMask)
			|| keyval is not (Gdk.Tab or Gdk.IsoLeftTab)) {
			return 0;
		}

		WebKit.webkit_web_view_evaluate_javascript(
			_webView, ReverseTabKeydown, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
		return 1;
	}
}
