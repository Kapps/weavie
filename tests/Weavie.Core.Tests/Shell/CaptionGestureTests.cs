using Weavie.Core.Shell;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class CaptionGestureTests {
	private static readonly CaptionPress Single = new(CaptionPress.Primary, 1);

	[Fact]
	public void AHeldPrimaryPressMayDrag() {
		var gesture = new CaptionGesture();
		gesture.NativePress(CaptionPress.Primary);

		Assert.Equal(CaptionAction.Drag, gesture.PagePress(Single));
	}

	[Fact]
	public void AReportAfterReleaseNeverDrags() {
		var gesture = new CaptionGesture();
		gesture.NativePress(CaptionPress.Primary);
		gesture.NativeRelease();

		Assert.Equal(CaptionAction.None, gesture.PagePress(Single));
	}

	[Fact]
	public void ADoubleClickCountsEvenWhenTheReportLandsAfterRelease() {
		var gesture = new CaptionGesture();
		gesture.NativePress(CaptionPress.Primary);
		gesture.NativeRelease();

		Assert.Equal(CaptionAction.DoubleClick, gesture.PagePress(new CaptionPress(CaptionPress.Primary, 2)));
	}

	[Fact]
	public void ATripleClickIsNotASecondDoubleClick() {
		var gesture = new CaptionGesture();
		gesture.NativePress(CaptionPress.Primary);

		Assert.Equal(CaptionAction.Drag, gesture.PagePress(new CaptionPress(CaptionPress.Primary, 3)));
	}

	[Theory]
	[InlineData(CaptionPress.Middle, CaptionAction.MiddleClick)]
	[InlineData(CaptionPress.Secondary, CaptionAction.SecondaryClick)]
	public void OtherButtonsRunTheirClickAction(int button, CaptionAction expected) {
		var gesture = new CaptionGesture();
		gesture.NativePress(button);
		gesture.NativeRelease();

		Assert.Equal(expected, gesture.PagePress(new CaptionPress(button, 1)));
	}

	[Fact]
	public void AReportClaimsItsPressOnce() {
		var gesture = new CaptionGesture();
		gesture.NativePress(CaptionPress.Secondary);

		Assert.Equal(CaptionAction.SecondaryClick, gesture.PagePress(new CaptionPress(CaptionPress.Secondary, 1)));
		Assert.Equal(CaptionAction.None, gesture.PagePress(new CaptionPress(CaptionPress.Secondary, 1)));
	}

	[Fact]
	public void AReportWithoutAMatchingPressDoesNothing() {
		var gesture = new CaptionGesture();
		Assert.Equal(CaptionAction.None, gesture.PagePress(Single));

		gesture.NativePress(CaptionPress.Secondary);
		Assert.Equal(CaptionAction.None, gesture.PagePress(Single));
	}

	[Fact]
	public void UnknownButtonsAreIgnored() {
		var gesture = new CaptionGesture();
		gesture.NativePress(4);

		Assert.Equal(CaptionAction.None, gesture.PagePress(new CaptionPress(4, 1)));
	}
}
