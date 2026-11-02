using System.Text.Json.Nodes;
using Weavie.Core.Workflows;
using Xunit;

namespace Weavie.Core.Tests.Workflows;

public sealed class YamlNodeReaderTests {
	[Fact]
	public void ScalarsStayStringsSoYesAndNumbersKeepTheirSpelling() {
		var document = YamlNodeReader.Read("choices: { yes: fix, no: done }\nmax: 3\nflag: true\n");
		var root = document.Root!.AsObject();
		Assert.Equal("fix", root["choices"]!["yes"]!.GetValue<string>());
		Assert.Equal("3", root["max"]!.GetValue<string>());
		Assert.Equal("true", root["flag"]!.GetValue<string>());
	}

	[Fact]
	public void EmptyPlainScalarsReadAsNullButQuotedOnesStayText() {
		var root = YamlNodeReader.Read("a:\nb: ~\nc: ''\n").Root!.AsObject();
		Assert.Null(root["a"]);
		Assert.Null(root["b"]);
		Assert.Equal("", root["c"]!.GetValue<string>());
	}

	[Fact]
	public void BlockScalarsAndSequencesKeepTheirShape() {
		var root = YamlNodeReader.Read("text: |\n  one\n  two\nlist:\n  - a\n  - b\n").Root!.AsObject();
		Assert.Equal("one\ntwo\n", root["text"]!.GetValue<string>());
		Assert.Equal(["a", "b"], root["list"]!.AsArray().Select(item => item!.GetValue<string>()));
	}

	[Fact]
	public void PositionsAreRecordedAtEachKey() {
		var document = YamlNodeReader.Read("id: x\nstages:\n  - id: a\n    do: { prompt: hi }\n");
		Assert.Equal(new WorkflowPosition(2, 1), document.Positions["stages"]);
		Assert.Equal(new WorkflowPosition(4, 5), document.Positions["stages[0].do"]);
		Assert.Equal(new WorkflowPosition(4, 11), document.Positions["stages[0].do.prompt"]);
	}

	[Theory]
	[InlineData("a: &x 1\nb: *x\n", "anchors")]
	[InlineData("a: !custom 1\n", "anchors and tags")]
	[InlineData("a: 1\n---\nb: 2\n", "exactly one YAML document")]
	[InlineData("a: 1\na: 2\n", "Duplicate key")]
	[InlineData("", "empty")]
	[InlineData("a: [1, 2\n", "")]
	public void RejectsWhatWorkflowsDoNotSupport(string text, string message) {
		var ex = Assert.Throws<WorkflowFormatException>(() => YamlNodeReader.Read(text));
		Assert.Contains(message, ex.Message, StringComparison.Ordinal);
		Assert.True(ex.Position.Line >= 1);
	}

	[Fact]
	public void NestedMappingsBecomeJsonObjects() {
		var root = YamlNodeReader.Read("a:\n  b:\n    c: d\n").Root;
		Assert.Equal("""{"a":{"b":{"c":"d"}}}""", root!.ToJsonString());
		Assert.IsType<JsonObject>(root["a"]);
	}
}
