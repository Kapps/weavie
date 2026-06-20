using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The observed-mode mirror: Weavie reflects Claude's reported <c>permission_mode</c> off the hook stream and
/// reports whether edits are auto-applying. Also covers <see cref="HookRequest.Parse"/> reading the field.
/// </summary>
public sealed class ObservedPermissionModeTests {
	private static HookRequest Pre(string? mode) =>
		new() { Event = HookEventKind.PreToolUse, ToolName = "Edit", ToolInputJson = "{}", PermissionMode = mode };

	[Fact]
	public void DefaultsToDefault_BeforeAnyObservation() {
		var observed = new ObservedPermissionMode();
		Assert.Equal("default", observed.Current);
		Assert.False(observed.AutoAppliesEdits);
	}

	[Theory]
	[InlineData("acceptEdits", true)]
	[InlineData("bypassPermissions", true)]
	[InlineData("default", false)]
	[InlineData("plan", false)]
	public void Observe_TracksReportedMode(string mode, bool autoApplies) {
		var observed = new ObservedPermissionMode();
		observed.Observe(Pre(mode));
		Assert.Equal(mode, observed.Current);
		Assert.Equal(autoApplies, observed.AutoAppliesEdits);
	}

	[Fact]
	public void Observe_KeepsLastKnownMode_WhenEventOmitsIt() {
		var observed = new ObservedPermissionMode();
		observed.Observe(Pre("acceptEdits"));
		observed.Observe(Pre(null)); // an event that doesn't carry permission_mode
		Assert.Equal("acceptEdits", observed.Current);
	}

	[Fact]
	public void Parse_ReadsPermissionMode() {
		var request = HookRequest.Parse(
			"{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"permission_mode\":\"acceptEdits\"}");
		Assert.NotNull(request);
		Assert.Equal("acceptEdits", request!.PermissionMode);
	}

	[Fact]
	public void Parse_PermissionModeAbsent_IsNull() {
		var request = HookRequest.Parse("{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\"}");
		Assert.NotNull(request);
		Assert.Null(request!.PermissionMode);
	}
}
