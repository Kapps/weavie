using Weavie.Core.Shell;
using Weavie.Linux.Hosting;
using Xunit;
using static Weavie.Core.Shell.WindowControl;

namespace Weavie.Linux.Tests;

public sealed class DecorationLayoutTests {
	public static TheoryData<string, WindowControl[], WindowControl[]> Layouts => new() {
		{ "menu:minimize,maximize,close", [], [Minimize, MaximizeToggle, Close] },
		{ "close,minimize,maximize:", [Close, Minimize, MaximizeToggle], [] },
		{ "appmenu:close", [], [Close] },
		{ "icon , close :menu", [Close], [] },
		{ "menu:", [], [] },
		{ "close", [Close], [] },
		{ ":spacer,maximize,unknown", [], [MaximizeToggle] },
	};

	[Theory]
	[MemberData(nameof(Layouts))]
	public void KeepsWindowButtonsPerSideInOrder(string layout, WindowControl[] start, WindowControl[] end) {
		var controls = DecorationLayout.Parse(layout);

		Assert.Equal(start, controls.Start);
		Assert.Equal(end, controls.End);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void NoLayoutMeansNoButtons(string? layout) => Assert.Same(WindowControls.None, DecorationLayout.Parse(layout));
}
