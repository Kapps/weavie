using Weavie.Core.Shell;

namespace Weavie.Linux.Hosting;

/// <summary>What a press on empty title-bar space turned into.</summary>
internal enum CaptionAction {
	/// <summary>Nothing (yet): an unpaired report, or a single press waiting for the pointer to move.</summary>
	None,

	/// <summary>Hand the held press to the window system as a move.</summary>
	Move,

	/// <summary>Run the desktop's title-bar double-click action.</summary>
	DoubleClick,

	/// <summary>Run the desktop's title-bar middle-click action.</summary>
	MiddleClick,

	/// <summary>Run the desktop's title-bar right-click action.</summary>
	SecondaryClick,
}

/// <summary>
/// Pairs the page's caption reports with the native presses GTK delivered. A report claims the latest unconsumed press
/// of its button, so a quick click whose report lands after the release still counts; only a move needs the button
/// still held, and it starts once the pointer passes the drag threshold so a double-click stays possible.
/// </summary>
internal sealed class CaptionGesture {
	private uint _pressed;
	private bool _held;
	private bool _armed;

	/// <summary>A native button press (GDK button number, 1 = primary).</summary>
	public void NativePress(uint button) {
		_pressed = button;
		_held = true;
		_armed = false;
	}

	/// <summary>The native button came up.</summary>
	public void NativeRelease() {
		_held = false;
		_armed = false;
	}

	/// <summary>The pointer moved; <paramref name="pastThreshold"/> says whether it has left the press's drag threshold.</summary>
	public CaptionAction NativeMotion(bool pastThreshold) {
		if (!_armed || !_held || !pastThreshold) {
			return CaptionAction.None;
		}

		_armed = false;
		return CaptionAction.Move;
	}

	/// <summary>The page reported a press on empty bar space.</summary>
	public CaptionAction PagePress(CaptionPress press) {
		uint button = press.Button switch {
			CaptionPress.Primary => 1,
			CaptionPress.Middle => 2,
			CaptionPress.Secondary => 3,
			_ => 0,
		};
		if (button == 0 || button != _pressed) {
			return CaptionAction.None;
		}

		_pressed = 0;
		return button switch {
			1 when press.Count >= 2 => CaptionAction.DoubleClick,
			1 => Arm(),
			2 => CaptionAction.MiddleClick,
			_ => CaptionAction.SecondaryClick,
		};
	}

	private CaptionAction Arm() {
		_armed = _held;
		return CaptionAction.None;
	}
}
