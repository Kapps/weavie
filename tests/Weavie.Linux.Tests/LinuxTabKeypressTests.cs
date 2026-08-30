using Weavie.Linux.Native;
using Xunit;

namespace Weavie.Linux.Tests;

public sealed class LinuxTabKeypressTests {
	[Theory]
	[InlineData(Gdk.Tab)]
	[InlineData(Gdk.IsoLeftTab)]
	public void CtrlTab_ForwardsTheFirstPressToTheWebResolver(uint keyval) {
		string script = Assert.IsType<string>(WorkspaceHost.TabKeydownScript(keyval, Gdk.ControlMask));

		Assert.Contains("key:'Tab'", script, StringComparison.Ordinal);
		Assert.DoesNotContain("shiftKey:true", script, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(Gdk.Tab)]
	[InlineData(Gdk.IsoLeftTab)]
	public void CtrlShiftTab_ForwardsReverseNavigation(uint keyval) {
		string script = Assert.IsType<string>(
			WorkspaceHost.TabKeydownScript(keyval, Gdk.ControlMask | Gdk.ShiftMask));

		Assert.Contains("key:'ISO_Left_Tab'", script, StringComparison.Ordinal);
		Assert.Contains("shiftKey:true", script, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(Gdk.Tab, 0u)]
	[InlineData(Gdk.Tab, Gdk.ShiftMask)]
	[InlineData(Gdk.Tab, Gdk.ControlMask | Gdk.AltMask)]
	[InlineData(0xff0d, Gdk.ControlMask)]
	public void OtherChords_StayWithWebKit(uint keyval, uint state) => Assert.Null(WorkspaceHost.TabKeydownScript(keyval, state));
}
