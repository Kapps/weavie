using System.Text.Json;
using Weavie.TestSupport;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowTemplateValueTests {
	[Fact]
	public void TemplatesListReferencesAndRenderValues() {
		Assert.True(WorkflowTemplate.TryParse("Do {{inputs.task}} then {{ spec.tasks }}.", out var template, out _));
		Assert.Equal([new WorkflowReference("inputs", "task"), new WorkflowReference("spec", "tasks")], template.References);
		string text = template.Render(reference => reference.IsInput
			? new WorkflowValue { Text = "X" }
			: new WorkflowValue { Items = ["a", "b"] });
		Assert.Equal("Do X then - a\n- b.", text);
	}

	[Fact]
	public void TemplateWithoutReferencesRendersAsIs() {
		Assert.True(WorkflowTemplate.TryParse("plain", out var template, out _));
		Assert.Empty(template.References);
		Assert.Equal("plain", template.Render(_ => throw new InvalidOperationException()));
	}

	[Theory]
	[InlineData("text", "\"hi\"", true)]
	[InlineData("text", "3", false)]
	[InlineData("url", "\"https://example.com/x\"", true)]
	[InlineData("url", "\"ftp://example.com\"", false)]
	[InlineData("url", "\"example.com\"", false)]
	[InlineData("pr", "\"https://github.com/o/r/pull/1\"", true)]
	[InlineData("list", "[\"a\", \"b\"]", true)]
	[InlineData("list", "[\"a\", 1]", false)]
	[InlineData("list", "[\" \"]", false)]
	[InlineData("list", "\"a\"", false)]
	public void ValuesAreTypeChecked(string type, string json, bool valid) {
		using var document = JsonDocument.Parse(json);
		bool ok = WorkflowValues.TryRead(document.RootElement, WorkflowValues.ParseType(type), "/", out _, out string error);
		Assert.Equal(valid, ok);
		Assert.Equal(valid, error.Length == 0);
	}

	[Fact]
	public void FilesMustBeNonEmptyAndInsideTheWorktree() {
		using var dir = new TempDirectory("weavie-workflow-values");
		dir.WriteFile("videos/a.mp4", "data");
		dir.WriteFile("empty.txt", "");
		bool Read(string json) {
			using var document = JsonDocument.Parse(json);
			return WorkflowValues.TryRead(document.RootElement, WorkflowValueType.Files, dir.Path, out _, out _);
		}

		Assert.True(Read("[\"videos/a.mp4\"]"));
		Assert.False(Read("[\"empty.txt\"]"));
		Assert.False(Read("[\"missing.mp4\"]"));
		Assert.False(Read("[\"../outside\"]"));
		Assert.False(Read($"[\"{JsonEncodedText.Encode(dir.Combine("videos", "a.mp4"))}\"]"));
	}
}
