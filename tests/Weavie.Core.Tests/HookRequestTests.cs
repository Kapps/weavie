using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Parsing the hook stdin payload into a <see cref="HookRequest"/>: tool + raw input out, junk to null.</summary>
public sealed class HookRequestTests {
	[Fact]
	public void Parse_PreToolUseBash_ExtractsToolAndInput() {
		string json = """
			{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"ls -la"},"session_id":"s1","cwd":"/w"}
			""";

		var request = HookRequest.Parse(json);

		Assert.NotNull(request);
		Assert.Equal(HookEventKind.PreToolUse, request!.Event);
		Assert.Equal("Bash", request.ToolName);
		Assert.Contains("ls -la", request.ToolInputJson, StringComparison.Ordinal);
		Assert.Equal("s1", request.SessionId);
		Assert.Equal("/w", request.Cwd);
	}

	[Fact]
	public void Parse_PostToolUseEdit_MapsEvent() {
		var request = HookRequest.Parse("""{"hook_event_name":"PostToolUse","tool_name":"Edit","tool_input":{"file_path":"/a"}}""");

		Assert.NotNull(request);
		Assert.Equal(HookEventKind.PostToolUse, request!.Event);
		Assert.Equal("Edit", request.ToolName);
	}

	[Fact]
	public void Parse_UnknownEvent_MapsToOther() {
		var request = HookRequest.Parse("""{"hook_event_name":"SessionStart","tool_name":"X"}""");

		Assert.NotNull(request);
		Assert.Equal(HookEventKind.Other, request!.Event);
	}

	[Fact]
	public void Parse_UserPromptSubmit_ParsesWithoutToolName() {
		var request = HookRequest.Parse("""{"hook_event_name":"UserPromptSubmit","prompt":"hi","session_id":"s1","cwd":"/w"}""");

		Assert.NotNull(request);
		Assert.Equal(HookEventKind.UserPromptSubmit, request!.Event);
		Assert.Equal(string.Empty, request.ToolName);
		Assert.Equal("/w", request.Cwd);
	}

	[Fact]
	public void Parse_Stop_ParsesWithoutToolName() {
		var request = HookRequest.Parse("""{"hook_event_name":"Stop","session_id":"s1"}""");

		Assert.NotNull(request);
		Assert.Equal(HookEventKind.Stop, request!.Event);
		Assert.Equal(string.Empty, request.ToolName);
	}

	[Fact]
	public void Parse_MissingToolInput_DefaultsToEmptyObject() {
		var request = HookRequest.Parse("""{"hook_event_name":"PreToolUse","tool_name":"Bash"}""");

		Assert.NotNull(request);
		Assert.Equal("{}", request!.ToolInputJson);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("not json")]
	[InlineData("{}")]
	[InlineData("[1,2,3]")]
	[InlineData("""{"tool_name":""}""")]
	public void Parse_InvalidOrNameless_ReturnsNull(string json) =>
		Assert.Null(HookRequest.Parse(json));
}
