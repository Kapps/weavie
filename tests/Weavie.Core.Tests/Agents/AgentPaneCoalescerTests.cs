using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="AgentPaneCoalescer"/>'s deterministic paths: a burst buffers until an explicit flush and
/// posts as one ordered batch; discard drops the buffer without posting; a zero window posts each message inline;
/// and dispose drops the buffer and silences later calls. The time-window flush is covered end-to-end (a real
/// timer would make a unit test timing-bound); a large window keeps the timer dormant during these synchronous
/// tests.
/// </summary>
public sealed class AgentPaneCoalescerTests {
	private const int LongWindowMs = 600_000;

	private readonly List<IReadOnlyList<AgentPaneMessage>> _posts = [];

	private AgentPaneCoalescer Coalescer(int windowMs) => new(batch => _posts.Add(batch), windowMs);

	private static AgentPaneMessage Msg(string text) =>
		new() { Type = "item-completed", ProviderId = "codex", Text = text };

	[Fact]
	public void Burst_BuffersUntilFlush_ThenPostsOneOrderedBatch() {
		using var c = Coalescer(LongWindowMs);

		c.Add(Msg("one"));
		c.Add(Msg("two"));
		c.Add(Msg("three"));
		Assert.Empty(_posts);

		c.Flush();
		var batch = Assert.Single(_posts);
		Assert.Equal(["one", "two", "three"], batch.Select(m => m.Text));
	}

	[Fact]
	public void Flush_WhenEmpty_PostsNothing() {
		using var c = Coalescer(LongWindowMs);

		c.Flush();

		Assert.Empty(_posts);
	}

	[Fact]
	public void Discard_DropsBufferedMessages() {
		using var c = Coalescer(LongWindowMs);
		c.Add(Msg("gone"));

		c.Discard();

		c.Flush();
		Assert.Empty(_posts);
	}

	[Fact]
	public void ZeroWindow_PostsEachMessageInline_AsSingletonBatches() {
		using var c = Coalescer(0);

		c.Add(Msg("a"));
		c.Add(Msg("b"));

		Assert.Equal(2, _posts.Count);
		Assert.Equal("a", Assert.Single(_posts[0]).Text);
		Assert.Equal("b", Assert.Single(_posts[1]).Text);
	}

	[Fact]
	public void Dispose_DropsBuffer_AndSilencesLaterCalls() {
		var c = Coalescer(LongWindowMs);
		c.Add(Msg("pending"));

		c.Dispose();

		c.Flush();
		c.Add(Msg("after"));
		Assert.Empty(_posts);
	}
}
