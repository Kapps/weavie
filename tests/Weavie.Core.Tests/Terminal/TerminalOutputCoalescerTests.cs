using System.Text;
using Weavie.Core.Terminal;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="TerminalOutputCoalescer"/>'s deterministic paths: a sub-threshold burst buffers until an
/// explicit flush and posts as one ordered frame; crossing the byte threshold flushes inline; discard drops the
/// buffer without posting; a zero window posts every chunk inline; and dispose drops the buffer and silences
/// later calls. The time-window flush is covered end-to-end (a real timer would make a unit test timing-bound).
/// A large window keeps the timer from firing during these synchronous tests.
/// </summary>
public sealed class TerminalOutputCoalescerTests {
	private const int LongWindowMs = 600_000;
	private const int ThresholdBytes = 256 * 1024;

	private readonly List<byte[]> _posts = [];

	private TerminalOutputCoalescer Coalescer(int windowMs) => new(bytes => _posts.Add(bytes), windowMs);

	private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

	[Fact]
	public void SubThresholdBurst_BuffersUntilFlush_ThenPostsOneOrderedFrame() {
		using var c = Coalescer(LongWindowMs);

		c.Add(Text("one "));
		c.Add(Text("two "));
		c.Add(Text("three"));
		Assert.Empty(_posts);

		c.Flush();
		Assert.Equal("one two three", Encoding.UTF8.GetString(Assert.Single(_posts)));
	}

	[Fact]
	public void Flush_WhenEmpty_PostsNothing() {
		using var c = Coalescer(LongWindowMs);

		c.Flush();

		Assert.Empty(_posts);
	}

	[Fact]
	public void EmptyChunk_IsIgnored() {
		using var c = Coalescer(LongWindowMs);

		c.Add([]);

		c.Flush();
		Assert.Empty(_posts);
	}

	[Fact]
	public void CrossingByteThreshold_FlushesInline_AsOneFrame() {
		using var c = Coalescer(LongWindowMs);

		c.Add(new byte[ThresholdBytes - 1024]);
		Assert.Empty(_posts); // still under the threshold

		c.Add(new byte[2048]); // crosses it
		Assert.Equal(ThresholdBytes + 1024, Assert.Single(_posts).Length);

		c.Add(Text("after"));
		Assert.Single(_posts); // the post-threshold chunk buffers again
	}

	[Fact]
	public void Discard_DropsBufferedOutput() {
		using var c = Coalescer(LongWindowMs);
		c.Add(Text("gone"));

		c.Discard();

		c.Flush();
		Assert.Empty(_posts);
	}

	[Fact]
	public void ZeroWindow_PostsEachChunkInline() {
		using var c = Coalescer(0);

		c.Add(Text("a"));
		c.Add(Text("b"));

		Assert.Equal(2, _posts.Count);
		Assert.Equal("a", Encoding.UTF8.GetString(_posts[0]));
		Assert.Equal("b", Encoding.UTF8.GetString(_posts[1]));
	}

	[Fact]
	public void Dispose_DropsBuffer_AndSilencesLaterCalls() {
		var c = Coalescer(LongWindowMs);
		c.Add(Text("pending"));

		c.Dispose();

		c.Flush();
		c.Add(Text("after"));
		Assert.Empty(_posts);
	}
}
