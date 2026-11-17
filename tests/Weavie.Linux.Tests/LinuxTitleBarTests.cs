using Weavie.Core.Shell;
using Weavie.Linux.Hosting;
using Xunit;

namespace Weavie.Linux.Tests;

public sealed class DecorationLayoutTests {
	[Theory]
	[InlineData("menu:minimize,maximize,close", "", "minimize,maximize-toggle,close")]
	[InlineData("close,minimize,maximize:", "close,minimize,maximize-toggle", "")]
	[InlineData("appmenu:close", "", "close")]
	[InlineData("icon , close :menu", "close", "")]
	[InlineData("menu:", "", "")]
	[InlineData("close", "close", "")]
	[InlineData(":spacer,maximize,unknown", "", "maximize-toggle")]
	public void KeepsWindowButtonsPerSideInOrder(string layout, string start, string end) {
		var controls = DecorationLayout.Parse(layout);

		Assert.Equal(start, Names(controls.Start));
		Assert.Equal(end, Names(controls.End));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void NoLayoutMeansNoButtons(string? layout) => Assert.Same(WindowControls.None, DecorationLayout.Parse(layout));

	private static string Names(IReadOnlyList<WindowControl> controls) => string.Join(",", controls.Select(control => control switch {
		WindowControl.Minimize => "minimize",
		WindowControl.MaximizeToggle => "maximize-toggle",
		WindowControl.Close => "close",
		_ => throw new ArgumentOutOfRangeException(nameof(control)),
	}));
}

public sealed class CaptionGestureTests {
	private static readonly CaptionPress Single = new(CaptionPress.Primary, 1);

	[Fact]
	public void SinglePressMovesOncePastTheThreshold() {
		var gesture = new CaptionGesture();
		gesture.NativePress(1);

		Assert.Equal(CaptionAction.None, gesture.PagePress(Single));
		Assert.Equal(CaptionAction.None, gesture.NativeMotion(pastThreshold: false));
		Assert.Equal(CaptionAction.Move, gesture.NativeMotion(pastThreshold: true));
		Assert.Equal(CaptionAction.None, gesture.NativeMotion(pastThreshold: true));
	}

	[Fact]
	public void MotionBeforeTheReportDoesNotMove() {
		var gesture = new CaptionGesture();
		gesture.NativePress(1);

		Assert.Equal(CaptionAction.None, gesture.NativeMotion(pastThreshold: true));
	}

	[Fact]
	public void ReportAfterReleaseNeverMoves() {
		var gesture = new CaptionGesture();
		gesture.NativePress(1);
		gesture.NativeRelease();

		Assert.Equal(CaptionAction.None, gesture.PagePress(Single));
		Assert.Equal(CaptionAction.None, gesture.NativeMotion(pastThreshold: true));
	}

	[Fact]
	public void ReleaseDisarmsAPendingMove() {
		var gesture = new CaptionGesture();
		gesture.NativePress(1);
		gesture.PagePress(Single);
		gesture.NativeRelease();

		Assert.Equal(CaptionAction.None, gesture.NativeMotion(pastThreshold: true));
	}

	[Fact]
	public void DoubleClickCountsEvenWhenTheReportLandsAfterRelease() {
		var gesture = new CaptionGesture();
		gesture.NativePress(1);
		gesture.NativeRelease();

		Assert.Equal(CaptionAction.DoubleClick, gesture.PagePress(new CaptionPress(CaptionPress.Primary, 2)));
	}

	[Theory]
	[InlineData(CaptionPress.Middle, 2u, "MiddleClick")]
	[InlineData(CaptionPress.Secondary, 3u, "SecondaryClick")]
	public void OtherButtonsRunTheirClickAction(int domButton, uint gdkButton, string expected) {
		var gesture = new CaptionGesture();
		gesture.NativePress(gdkButton);
		gesture.NativeRelease();

		Assert.Equal(expected, gesture.PagePress(new CaptionPress(domButton, 1)).ToString());
	}

	[Fact]
	public void AReportClaimsItsPressOnce() {
		var gesture = new CaptionGesture();
		gesture.NativePress(3);

		Assert.Equal(CaptionAction.SecondaryClick, gesture.PagePress(new CaptionPress(CaptionPress.Secondary, 1)));
		Assert.Equal(CaptionAction.None, gesture.PagePress(new CaptionPress(CaptionPress.Secondary, 1)));
	}

	[Theory]
	[InlineData(0u)]
	[InlineData(3u)]
	public void AReportWithoutAMatchingPressDoesNothing(uint pressed) {
		var gesture = new CaptionGesture();
		if (pressed != 0) {
			gesture.NativePress(pressed);
		}

		Assert.Equal(CaptionAction.None, gesture.PagePress(Single));
		Assert.Equal(CaptionAction.None, gesture.NativeMotion(pastThreshold: true));
	}

	[Fact]
	public void UnknownDomButtonsAreIgnored() {
		var gesture = new CaptionGesture();
		gesture.NativePress(1);

		Assert.Equal(CaptionAction.None, gesture.PagePress(new CaptionPress(4, 1)));
	}
}
