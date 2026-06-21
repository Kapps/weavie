using Weavie.Core.Documents;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class TextBufferTests {
	[Fact]
	public void NormalizesCrlfToLf() {
		var buffer = new TextBuffer("a\r\nb\rc");
		Assert.Equal("a\nb\nc", buffer.Text);
		Assert.Equal(3, buffer.LineCount);
	}

	[Fact]
	public void GetText_ReadsAcrossLines_WithMonacoColumns() {
		// Lines: "hello"(1) "world"(2). Column 1 is before the first char.
		var buffer = new TextBuffer("hello\nworld");
		var range = new TextRange(new Position(1, 1), new Position(2, 6));
		Assert.Equal("hello\nworld", buffer.GetText(range));

		var inner = new TextRange(new Position(1, 2), new Position(2, 3));
		Assert.Equal("ello\nwo", buffer.GetText(inner));
	}

	[Fact]
	public void Apply_InsertAtPosition() {
		var buffer = new TextBuffer("ac");
		buffer.Apply(TextEdit.Insert(new Position(1, 2), "b"));
		Assert.Equal("abc", buffer.Text);
	}

	[Fact]
	public void Apply_ReplaceRange() {
		var buffer = new TextBuffer("hello world");
		var range = new TextRange(new Position(1, 7), new Position(1, 12));
		buffer.Apply(TextEdit.Replace(range, "there"));
		Assert.Equal("hello there", buffer.Text);
	}

	[Fact]
	public void Apply_DeleteRange_WithEmptyText() {
		var buffer = new TextBuffer("abcdef");
		var range = new TextRange(new Position(1, 3), new Position(1, 5));
		buffer.Apply(TextEdit.Replace(range, string.Empty));
		Assert.Equal("abef", buffer.Text);
	}

	[Fact]
	public void Apply_InsertNewline_UpdatesLineCount() {
		var buffer = new TextBuffer("ab");
		buffer.Apply(TextEdit.Insert(new Position(1, 2), "\n"));
		Assert.Equal("a\nb", buffer.Text);
		Assert.Equal(2, buffer.LineCount);
	}

	[Fact]
	public void Apply_BatchEdits_NonOverlapping_AppliedConsistently() {
		var buffer = new TextBuffer("0123456789");
		// Replace [1,2)->"A" (the '0') and [1,6) span "45" region: pick disjoint ranges.
		var edits = new[]
		{
			TextEdit.Replace(new TextRange(new Position(1, 1), new Position(1, 2)), "A"), // '0' -> 'A'
            TextEdit.Replace(new TextRange(new Position(1, 6), new Position(1, 7)), "B"), // '5' -> 'B'
        };
		buffer.Apply(edits);
		Assert.Equal("A1234B6789", buffer.Text);
	}

	[Fact]
	public void Apply_OverlappingBatch_Throws() {
		var buffer = new TextBuffer("0123456789");
		var edits = new[]
		{
			TextEdit.Replace(new TextRange(new Position(1, 1), new Position(1, 5)), "X"),
			TextEdit.Replace(new TextRange(new Position(1, 3), new Position(1, 7)), "Y"),
		};
		Assert.Throws<ArgumentException>(() => buffer.Apply(edits));
	}

	[Fact]
	public void OffsetOf_OutOfRangeLine_Throws() {
		var buffer = new TextBuffer("a\nb");
		var range = new TextRange(new Position(5, 1), new Position(5, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetText(range));
	}

	[Fact]
	public void OffsetOf_OutOfRangeColumn_Throws() {
		var buffer = new TextBuffer("ab");
		var range = new TextRange(new Position(1, 4), new Position(1, 4));
		Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetText(range));
	}

	[Fact]
	public void EndPosition_PointsPastLastCharacter() {
		var buffer = new TextBuffer("ab\ncde");
		Assert.Equal(new Position(2, 4), buffer.EndPosition);
	}
}
