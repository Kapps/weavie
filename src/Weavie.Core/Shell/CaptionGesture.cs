namespace Weavie.Core.Shell;

/// <summary>What a press on empty title-bar space turned into.</summary>
public enum CaptionAction {
	/// <summary>Nothing: no unclaimed native press of that button.</summary>
	None,

	/// <summary>The primary press is still held: the host may hand it to the window system as a move.</summary>
	Drag,

	/// <summary>Run the system's title-bar double-click action.</summary>
	DoubleClick,

	/// <summary>Run the system's title-bar middle-click action.</summary>
	MiddleClick,

	/// <summary>Run the system's title-bar right-click action.</summary>
	SecondaryClick,
}

/// <summary>
/// Pairs the page's caption reports with the native presses the host saw, both in DOM button numbers. A report claims
/// the latest unclaimed press of its button, so a quick click whose report lands after the release still counts; only
/// a drag needs the button still held.
/// </summary>
public sealed class CaptionGesture {
	private int? _pressed;
	private bool _held;

	/// <summary>A native button press, as a DOM button number (<see cref="CaptionPress.Primary"/> and so on).</summary>
	public void NativePress(int button) {
		_pressed = button;
		_held = true;
	}

	/// <summary>The native button came up.</summary>
	public void NativeRelease() => _held = false;

	/// <summary>The page reported a press on empty bar space.</summary>
	public CaptionAction PagePress(CaptionPress press) {
		if (press.Button != _pressed) {
			return CaptionAction.None;
		}

		_pressed = null;
		return press.Button switch {
			CaptionPress.Primary when press.Count == 2 => CaptionAction.DoubleClick,
			CaptionPress.Primary => _held ? CaptionAction.Drag : CaptionAction.None,
			CaptionPress.Middle => CaptionAction.MiddleClick,
			CaptionPress.Secondary => CaptionAction.SecondaryClick,
			_ => CaptionAction.None,
		};
	}
}
