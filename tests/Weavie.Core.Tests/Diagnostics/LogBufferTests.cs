using Weavie.Core.Diagnostics;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="LogBuffer"/> and its <see cref="LogBuffer.Tee"/> writer: ordered retention, oldest-first
/// eviction with a surfaced dropped count, snapshot isolation, and line-accurate console capture (the split that
/// backs the in-app log viewer) with forwarding to the wrapped writer left byte-for-byte intact.
/// </summary>
public sealed class LogBufferTests {
	[Fact]
	public void Snapshot_EmptyByDefault() {
		var (lines, dropped) = new LogBuffer(4).Snapshot();

		Assert.Empty(lines);
		Assert.Equal(0, dropped);
	}

	[Fact]
	public void Append_RetainsInOrderWithinCapacity() {
		var buffer = new LogBuffer(4);
		buffer.Append("a");
		buffer.Append("b");
		buffer.Append("c");

		var (lines, dropped) = buffer.Snapshot();
		Assert.Equal(["a", "b", "c"], lines);
		Assert.Equal(0, dropped);
	}

	[Fact]
	public void Overflow_EvictsOldestAndCountsDropped() {
		var buffer = new LogBuffer(3);
		string[] emitted = ["1", "2", "3", "4", "5"];
		foreach (string line in emitted) {
			buffer.Append(line);
		}

		var (lines, dropped) = buffer.Snapshot();
		Assert.Equal(["3", "4", "5"], lines); // only the most recent `capacity` survive
		Assert.Equal(2, dropped);              // the two evicted are surfaced, not silently lost
	}

	[Fact]
	public void Snapshot_IsAStableCopy() {
		var buffer = new LogBuffer(4);
		buffer.Append("first");
		var (lines, _) = buffer.Snapshot();
		buffer.Append("second");

		Assert.Equal(["first"], lines); // the earlier snapshot is unaffected by later appends
	}

	[Fact]
	public void Tee_CapturesEachLineAndForwardsUnchanged() {
		var buffer = new LogBuffer(16);
		var inner = new StringWriter();
		var tee = buffer.Tee(inner);

		tee.WriteLine("alpha");
		tee.WriteLine("beta");

		// Line-accurate capture: WriteLine routes through the overridden Write, so each line lands once.
		Assert.Equal(["alpha", "beta"], buffer.Snapshot().Lines);
		// Forwarding is byte-for-byte — the real console still shows exactly what was written.
		Assert.Equal($"alpha{Environment.NewLine}beta{Environment.NewLine}", inner.ToString());
	}

	[Fact]
	public void Tee_JoinsAPartialWriteWithItsTerminatingLine() {
		var buffer = new LogBuffer(16);
		var tee = buffer.Tee(new StringWriter());

		tee.Write("x");
		tee.WriteLine("y");

		Assert.Equal(["xy"], buffer.Snapshot().Lines); // a partial line holds until its newline arrives
	}

	[Fact]
	public void Tee_DropsCarriageReturnSoCrlfLandsClean() {
		var buffer = new LogBuffer(16);
		var tee = buffer.Tee(new StringWriter());

		tee.Write("crlf\r\nlf\n");

		Assert.Equal(["crlf", "lf"], buffer.Snapshot().Lines);
	}

	[Fact]
	public void Tee_HoldsAnUnterminatedTrailingLine() {
		var buffer = new LogBuffer(16);
		var tee = buffer.Tee(new StringWriter());

		tee.Write("done\n");
		tee.Write("no newline yet");

		Assert.Equal(["done"], buffer.Snapshot().Lines); // the trailing partial isn't captured until it ends
	}

	[Fact]
	public void Tee_WriteChar_AccumulatesAndForwards() {
		var buffer = new LogBuffer(16);
		var inner = new StringWriter();
		var tee = buffer.Tee(inner);

		foreach (char c in "hi\n") {
			tee.Write(c);
		}

		Assert.Equal(["hi"], buffer.Snapshot().Lines);
		Assert.Equal("hi\n", inner.ToString());
	}
}
