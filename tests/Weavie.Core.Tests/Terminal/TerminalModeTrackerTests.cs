using System.Text;
using Weavie.Core.Terminal;
using Xunit;

namespace Weavie.Core.Tests.Terminal;

/// <summary>
/// Pins down the mode-latching parser: what a reattaching client's restore preamble contains after the child's
/// output stream set (or reset) modes, across arbitrary chunk splits, and what is deliberately never latched.
/// </summary>
public sealed class TerminalModeTrackerTests {
	private static TerminalModeTracker Fed(params string[] chunks) {
		var tracker = new TerminalModeTracker();
		foreach (string chunk in chunks) {
			tracker.Feed(Encoding.UTF8.GetBytes(chunk));
		}

		return tracker;
	}

	private static string Restore(params string[] chunks) => Encoding.UTF8.GetString(Fed(chunks).BuildRestore());

	[Fact]
	public void LatchesClaudeFullscreenStartup_TitleThenAltScreenThenModesAscending() {
		// The measured claude 2.1.198 fullscreen startup set, in emission order.
		string restore = Restore("\x1b[?2004h\x1b[?1004h\x1b[?2031h\x1b]0;✳ Claude Code\x07\x1b[?1049h\x1b[?1000h\x1b[?1006h\x1b[?25l\x1b[4l");

		Assert.Equal("\x1b]0;✳ Claude Code\x07\x1b[?1049h\x1b[?25l\x1b[?1000h\x1b[?1004h\x1b[?1006h\x1b[?2004h\x1b[?2031h\x1b[4l", restore);
	}

	[Fact]
	public void LastWriteWins_IncludingTheResetDirection() {
		// ?25l after ?25h must replay the reset — a fresh xterm shows the cursor, so the DECRST is the state.
		Assert.Equal("\x1b[?25l", Restore("\x1b[?25h\x1b[?25l"));
		Assert.Equal("\x1b[?25h", Restore("\x1b[?25l\x1b[?25h"));
	}

	[Fact]
	public void MultiParamModeSet_LatchesEveryMode() =>
		Assert.Equal("\x1b[?1000h\x1b[?1006h", Restore("\x1b[?1000;1006h"));

	[Theory]
	[InlineData("\x1b[?10", "49h")] // split mid-params
	[InlineData("\x1b", "[?1049h")] // split at the ESC
	[InlineData("\x1b[", "?1049h")] // split after the CSI introducer
	[InlineData("\x1b[?1049", "h")] // split before the final byte
	public void CsiSplitAcrossChunks_StillLatches(string first, string second) =>
		Assert.Equal("\x1b[?1049h", Restore(first, second));

	[Fact]
	public void OscSplitAcrossChunks_IncludingSplitStTerminator_StillLatchesTitle() =>
		Assert.Equal("\x1b]0;split title\x07", Restore("\x1b]0;spl", "it ti", "tle\x1b", "\\"));

	// ?2026 (synchronized output) would freeze a client's rendering if replayed latched-on; ?1048 and
	// ESC 7/8 are per-frame cursor save/restore; DECSTBM (final 'r') is per-frame scroll-region state.
	[Fact]
	public void TransientState_IsNeverLatched() =>
		Assert.Equal("", Restore("\x1b[?2026h\x1b[?1048h\x1b7\x1b8\x1b[1;20r"));

	// DECSCUSR (intermediate space), kitty push/pop ('>'/'<' prefix), charset designation, keypad — all
	// swallowed; the ?2004h immediately after each must still latch cleanly.
	[Fact]
	public void NonModeSequences_AreConsumedWithoutCorruptingTheNext() =>
		Assert.Equal("\x1b[?2004h", Restore("\x1b[2 q\x1b[>1u\x1b[<u\x1b(0\x1b=\x1b[?2004h"));

	// OSC 7 (cwd) and OSC 52 (clipboard) must not become the title; a later real title wins.
	[Fact]
	public void OtherOscPayloads_AreSkippedWithoutRetention() =>
		Assert.Equal("\x1b]0;real\x07", Restore("\x1b]7;file:///tmp\x07\x1b]52;c;aGk=\x07\x1b]2;real\x07"));

	[Fact]
	public void DcsAndOtherStrings_AreSkippedToTheirTerminator() =>
		Assert.Equal("\x1b[?25l", Restore("\x1bP1$r0m\x1b\\\x1b[?25l"));

	// CAN aborts a sequence outright in the client's parser; trailing garbage must not complete it, and an
	// aborted OSC must not become the title.
	[Fact]
	public void CanAndSub_AbortWithoutLatching() =>
		Assert.Equal("", Restore("\x1b[?1049\x18 text ending in h\x1b]0;half a title\x1a more h"));

	// A '?' after params puts the client's parser in its ignore state; the sequence must not latch.
	[Fact]
	public void PrefixAfterParams_IsIgnoredLikeTheClient() =>
		Assert.Equal("", Restore("\x1b[1;?25h"));

	// ESC ESC discards the first ESC; the second still introduces a live sequence.
	[Fact]
	public void DoubledEscape_StillLatchesTheFollowingSequence() =>
		Assert.Equal("\x1b[?2004h", Restore("\x1b\x1b[?2004h"));

	[Fact]
	public void NothingLatched_BuildsEmpty() {
		Assert.Empty(Fed().BuildRestore());
		Assert.Empty(Fed("plain text, no escapes at all\r\n").BuildRestore());
		Assert.Empty(Fed("\x1b[2J\x1b[H\x1b[38;2;1;2;3m").BuildRestore()); // drawing, not modes
	}
}
