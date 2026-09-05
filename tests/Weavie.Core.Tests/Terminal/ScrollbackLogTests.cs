using System.Text;
using Weavie.Core.Terminal;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="ScrollbackLog"/> over real temp files (it is not behind <c>IFileSystem</c>): raw
/// replay of live output, boundary splitting faded history from live, escape sanitization, cap trimming at a
/// newline, a reopened log fading prior content, and an unwritable path disabling the log instead of throwing.
/// </summary>
public sealed class ScrollbackLogTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-scrollback-tests");

	private string LogPath => _dir.Combine("shell.log");

	public void Dispose() => _dir.Dispose();

	private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

	private static string Text(byte[] b) => Encoding.UTF8.GetString(b);

	[Fact]
	public void EmptyLog_BuildReplay_IsEmpty() {
		using var log = new ScrollbackLog(LogPath, 4096);
		Assert.Empty(log.BuildReplay());
	}

	[Fact]
	public void FreshRun_ReplaysLiveBytesRaw_NoFade() {
		using var log = new ScrollbackLog(LogPath, 4096);
		log.MarkBoundary();              // boundary 0; nothing logged yet
		log.Append(Bytes("hello\r\n"));

		// Boundary 0 → no faded region, no dim prefix/separator: live bytes verbatim.
		Assert.Equal("hello\r\n", Text(log.BuildReplay()));
	}

	[Fact]
	public void Boundary_SplitsFadedHistoryFromLive() {
		using var log = new ScrollbackLog(LogPath, 4096);
		log.Append(Bytes("old-run\r\n")); // before the boundary
		log.MarkBoundary();               // new process starts
		log.Append(Bytes("new-run\r\n")); // live output

		string replay = Text(log.BuildReplay());

		Assert.Contains("old-run", replay);
		Assert.Contains("new-run", replay);
		Assert.Contains("[90m", replay);      // faded region dimmed
		Assert.Contains("session resumed", replay); // faded/live separator

		// Live region after the separator, faded region before.
		int sep = replay.IndexOf("session resumed", StringComparison.Ordinal);
		Assert.Contains("old-run", replay[..sep]);
		Assert.DoesNotContain("new-run", replay[..sep]);
		Assert.Contains("new-run", replay[sep..]);
	}

	[Fact]
	public void FadedRegion_IsSanitized_DropsInnerEscapes() {
		using var log = new ScrollbackLog(LogPath, 4096);
		log.Append(Bytes("[31mred[0m\r\n")); // colored output before boundary
		log.MarkBoundary();
		log.Append(Bytes("live"));

		string replay = Text(log.BuildReplay());

		Assert.Contains("red", replay);          // text survives
		Assert.DoesNotContain("[31m", replay);   // inner color escape stripped (would override the dim)
	}

	[Fact]
	public void SanitizeForFaded_StripsCsiAndOsc_KeepsPrintableAndWhitespace() {
		byte[] input = Bytes("a[1;31mb]0;window titlec\td\r\n");
		Assert.Equal("abc\td\r\n", Text(ScrollbackLog.SanitizeForFaded(input)));
	}

	[Fact]
	public void SanitizeForFaded_StripsOscTerminatedByStringTerminator() {
		// OSC ended by ST (ESC \) instead of BEL must be consumed whole, leaving only the surrounding text.
		byte[] input = [(byte)'a', 0x1b, (byte)']', (byte)'0', (byte)';', (byte)'t', 0x1b, (byte)'\\', (byte)'b'];
		Assert.Equal("ab", Text(ScrollbackLog.SanitizeForFaded(input)));
	}

	[Fact]
	public void SanitizeForFaded_DropsStrayControlBytes() {
		// Bare control bytes (BEL, backspace, NUL, US) outside any escape must not survive into faded history.
		byte[] input = [(byte)'a', 0x07, 0x08, 0x00, 0x1f, (byte)'b'];
		Assert.Equal("ab", Text(ScrollbackLog.SanitizeForFaded(input)));
	}

	[Fact]
	public void Append_PastTwiceCap_TrimsFrontAtNewline_StaysBounded() {
		const int cap = 200;
		using var log = new ScrollbackLog(LogPath, cap);
		// 100 lines × 10 bytes ≈ 1000 bytes >> 2*cap, forcing repeated trims.
		for (int i = 0; i < 100; i++) {
			log.Append(Bytes($"line-{i:0000}\n"));
		}

		long size = new FileInfo(LogPath).Length;
		Assert.True(size <= 2 * cap, $"file should stay within twice the cap ({2 * cap}), was {size}");

		string remaining = Text(log.BuildReplay());
		Assert.Contains("line-0099", remaining);       // newest kept
		Assert.DoesNotContain("line-0000", remaining); // oldest trimmed
		Assert.StartsWith("line-", remaining);         // cut landed on a line boundary
	}

	[Fact]
	public void Reopen_TreatsPriorContentAsFaded() {
		using (var first = new ScrollbackLog(LogPath, 4096)) {
			first.MarkBoundary();
			first.Append(Bytes("from-previous-boot\r\n"));
		}

		// A fresh log over the same path starts its boundary at the existing length,
		// so prior content is faded until the next MarkBoundary.
		using var reopened = new ScrollbackLog(LogPath, 4096);
		string replay = Text(reopened.BuildReplay());

		Assert.Contains("from-previous-boot", replay);
		Assert.Contains("[90m", replay);      // faded
		Assert.Contains("session resumed", replay);
	}

	[Fact]
	public void UnwritablePath_DisablesLog_WithoutThrowing() {
		// Parent path is a FILE, so creating it as a directory fails and the log disables itself.
		string bad = Path.Combine(_dir.WriteFile("afile", "x"), "shell.log");

		using var log = new ScrollbackLog(bad, 4096);
		log.MarkBoundary();
		log.Append(Bytes("nothing"));

		Assert.Empty(log.BuildReplay()); // no crash, no content
	}
}
