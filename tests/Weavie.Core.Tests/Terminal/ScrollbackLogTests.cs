using System.Text;
using Weavie.Core.Terminal;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="ScrollbackLog"/> over real temp files (the log is not behind <c>IFileSystem</c>):
/// a fresh run replays its live output raw; a boundary splits faded history (sanitized + dimmed) from the
/// live current run; sanitization strips escape sequences; the cap trims the front at a newline and keeps
/// the file bounded; a reopened log treats prior content as faded; and an unwritable path disables the log
/// rather than throwing.
/// </summary>
public sealed class ScrollbackLogTests : IDisposable {
	private readonly string _dir;
	private readonly string _path;

	public ScrollbackLogTests() {
		_dir = Path.Combine(Path.GetTempPath(), "weavie-scrollback-tests", Guid.NewGuid().ToString("n"));
		_path = Path.Combine(_dir, "shell.log");
	}

	public void Dispose() {
		try {
			if (Directory.Exists(_dir)) {
				Directory.Delete(_dir, recursive: true);
			}
		} catch (IOException) {
			// best-effort cleanup
		}
	}

	private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

	private static string Text(byte[] b) => Encoding.UTF8.GetString(b);

	[Fact]
	public void EmptyLog_BuildReplay_IsEmpty() {
		using var log = new ScrollbackLog(_path, 4096);
		Assert.Empty(log.BuildReplay());
	}

	[Fact]
	public void FreshRun_ReplaysLiveBytesRaw_NoFade() {
		using var log = new ScrollbackLog(_path, 4096);
		log.MarkBoundary();              // process starts; nothing logged yet → boundary 0
		log.Append(Bytes("hello\r\n"));

		// boundary 0 → no faded region, no dim prefix/separator: the live bytes verbatim.
		Assert.Equal("hello\r\n", Text(log.BuildReplay()));
	}

	[Fact]
	public void Boundary_SplitsFadedHistoryFromLive() {
		using var log = new ScrollbackLog(_path, 4096);
		log.Append(Bytes("old-run\r\n")); // previous process output (before the boundary)
		log.MarkBoundary();               // a new process starts here
		log.Append(Bytes("new-run\r\n")); // current live output

		string replay = Text(log.BuildReplay());

		Assert.Contains("old-run", replay);
		Assert.Contains("new-run", replay);
		Assert.Contains("[90m", replay);      // faded region dimmed
		Assert.Contains("session resumed", replay); // separator between faded + live

		// The live region is after the separator and the faded region is before it.
		int sep = replay.IndexOf("session resumed", StringComparison.Ordinal);
		Assert.Contains("old-run", replay[..sep]);
		Assert.DoesNotContain("new-run", replay[..sep]);
		Assert.Contains("new-run", replay[sep..]);
	}

	[Fact]
	public void FadedRegion_IsSanitized_DropsInnerEscapes() {
		using var log = new ScrollbackLog(_path, 4096);
		log.Append(Bytes("[31mred[0m\r\n")); // colored previous output
		log.MarkBoundary();
		log.Append(Bytes("live"));

		string replay = Text(log.BuildReplay());

		Assert.Contains("red", replay);          // the text survives
		Assert.DoesNotContain("[31m", replay);   // but its inner color escape is stripped (would override the dim)
	}

	[Fact]
	public void SanitizeForFaded_StripsCsiAndOsc_KeepsPrintableAndWhitespace() {
		byte[] input = Bytes("a[1;31mb]0;window titlec\td\r\n");
		Assert.Equal("abc\td\r\n", Text(ScrollbackLog.SanitizeForFaded(input)));
	}

	[Fact]
	public void Append_PastTwiceCap_TrimsFrontAtNewline_StaysBounded() {
		const int cap = 200;
		using var log = new ScrollbackLog(_path, cap);
		// 100 lines of exactly 10 bytes each = ~1000 bytes >> 2*cap, forcing repeated trims.
		for (int i = 0; i < 100; i++) {
			log.Append(Bytes($"line-{i:0000}\n"));
		}

		long size = new FileInfo(_path).Length;
		Assert.True(size <= 2 * cap, $"file should stay within twice the cap ({2 * cap}), was {size}");

		string remaining = Text(log.BuildReplay());
		Assert.Contains("line-0099", remaining);       // most recent output kept
		Assert.DoesNotContain("line-0000", remaining); // oldest output trimmed away
		Assert.StartsWith("line-", remaining);         // cut landed on a line boundary (no split line/escape)
	}

	[Fact]
	public void Reopen_TreatsPriorContentAsFaded() {
		using (var first = new ScrollbackLog(_path, 4096)) {
			first.MarkBoundary();
			first.Append(Bytes("from-previous-boot\r\n"));
		}

		// A fresh log over the same path (a resumed session) starts its boundary at the existing length,
		// so the prior content is faded until the next MarkBoundary.
		using var reopened = new ScrollbackLog(_path, 4096);
		string replay = Text(reopened.BuildReplay());

		Assert.Contains("from-previous-boot", replay);
		Assert.Contains("[90m", replay);      // shown faded
		Assert.Contains("session resumed", replay);
	}

	[Fact]
	public void UnwritablePath_DisablesLog_WithoutThrowing() {
		// Make the log's parent a FILE, so creating it as a directory fails: the log disables itself.
		Directory.CreateDirectory(_dir);
		string fileAsParent = Path.Combine(_dir, "afile");
		File.WriteAllText(fileAsParent, "x");
		string bad = Path.Combine(fileAsParent, "shell.log");

		using var log = new ScrollbackLog(bad, 4096);
		log.MarkBoundary();
		log.Append(Bytes("nothing"));

		Assert.Empty(log.BuildReplay()); // no crash, no content
	}
}
