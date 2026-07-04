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
	public void Parse_PermissionRequest_MapsEventAndMode() {
		var request = HookRequest.Parse("""{"hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"rm -rf x"},"permission_mode":"default"}""");

		Assert.NotNull(request);
		Assert.Equal(HookEventKind.PermissionRequest, request!.Event);
		Assert.Equal("Bash", request.ToolName);
		Assert.Equal("default", request.PermissionMode);
	}

	[Fact]
	public void Parse_UnknownEvent_MapsToOther() {
		var request = HookRequest.Parse("""{"hook_event_name":"PreCompact","tool_name":"X"}""");

		Assert.NotNull(request);
		Assert.Equal(HookEventKind.Other, request!.Event);
	}

	[Fact]
	public void Parse_SessionStartClear_ParsesSourceWithoutToolName() {
		var request = HookRequest.Parse("""{"hook_event_name":"SessionStart","source":"clear","session_id":"s2","cwd":"/w"}""");

		Assert.NotNull(request);
		Assert.Equal(HookEventKind.SessionStart, request!.Event);
		Assert.Equal("clear", request.Source);
		Assert.Equal(string.Empty, request.ToolName);
		Assert.Equal("s2", request.SessionId);
	}

	[Fact]
	public void Parse_Notification_CapturesMessageWithoutToolName() {
		var request = HookRequest.Parse("""{"hook_event_name":"Notification","message":"Claude is waiting for your input","session_id":"s1"}""");

		Assert.NotNull(request);
		Assert.Equal(HookEventKind.Notification, request!.Event);
		Assert.Equal("Claude is waiting for your input", request.Message);
		Assert.Equal(string.Empty, request.ToolName);
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
		// No task registry in the payload → nothing pending.
		Assert.False(request.SessionWillResume);
	}

	[Fact]
	public void Parse_Stop_EmptyRegistries_SessionWillNotResume() {
		var request = HookRequest.Parse("""{"hook_event_name":"Stop","background_tasks":[],"session_crons":[]}""");

		Assert.NotNull(request);
		Assert.False(request!.SessionWillResume);
	}

	[Fact]
	public void Parse_Stop_OneShotWakeup_SessionWillResume() {
		// A dynamic loop / ScheduleWakeup arms a one-shot cron (recurring:false) — the overnight-loss case.
		var request = HookRequest.Parse(
			"""{"hook_event_name":"Stop","session_crons":[{"id":"c1","recurring":false,"prompt":"check CI"}]}""");

		Assert.NotNull(request);
		Assert.True(request!.SessionWillResume);
	}

	[Fact]
	public void Parse_Stop_RecurringCronOnly_SessionWillNotResume() {
		// A standing recurring routine survives a restart and would hold the update forever — it must not count.
		var request = HookRequest.Parse(
			"""{"hook_event_name":"Stop","session_crons":[{"id":"c1","recurring":true,"schedule":"0 9 * * 1-5"}]}""");

		Assert.NotNull(request);
		Assert.False(request!.SessionWillResume);
	}

	[Fact]
	public void Parse_Stop_RunningBackgroundTask_SessionWillResume() {
		var request = HookRequest.Parse(
			"""{"hook_event_name":"Stop","background_tasks":[{"id":"t1","status":"running","command":"sleep 900"}]}""");

		Assert.NotNull(request);
		Assert.True(request!.SessionWillResume);
	}

	[Fact]
	public void Parse_Stop_RecurringCronPlusOneShot_SessionWillResume() {
		// Mixed registry: the one-shot entry still holds even alongside a standing recurring routine.
		var request = HookRequest.Parse(
			"""{"hook_event_name":"Stop","session_crons":[{"recurring":true},{"recurring":false}]}""");

		Assert.NotNull(request);
		Assert.True(request!.SessionWillResume);
	}

	[Fact]
	public void Parse_NonStopEvent_IgnoresRegistries() {
		// The registries only mean "will resume" at turn end; a mid-turn tool event carrying them says nothing.
		var request = HookRequest.Parse(
			"""{"hook_event_name":"PostToolUse","tool_name":"Bash","session_crons":[{"recurring":false}]}""");

		Assert.NotNull(request);
		Assert.False(request!.SessionWillResume);
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
