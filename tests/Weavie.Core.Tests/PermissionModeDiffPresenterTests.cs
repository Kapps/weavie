using Weavie.Core.Diffs;
using Weavie.Core.Hooks;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The openDiff auto-keep policy, keyed on Claude's OBSERVED edit mode (not a Weavie setting): <c>default</c>
/// delegates to the inner presenter (the blocking review); <c>acceptEdits</c>/<c>bypassPermissions</c>
/// auto-keep without consulting it.
/// </summary>
public sealed class PermissionModeDiffPresenterTests {
	private static DiffProposal Proposal() => new("/a.txt", "/a.txt", "new", "a.txt");

	private static ObservedPermissionMode Mode(string mode) {
		var observed = new ObservedPermissionMode();
		observed.Observe(new HookRequest {
			Event = HookEventKind.PreToolUse,
			ToolName = "Edit",
			ToolInputJson = "{}",
			PermissionMode = mode,
		});
		return observed;
	}

	[Fact]
	public async Task Default_DelegatesToInnerPresenter() {
		var inner = FakeDiffPresenter.AlwaysReject();
		var presenter = new PermissionModeDiffPresenter(inner, new ObservedPermissionMode()); // default

		var outcome = await presenter.PresentDiffAsync(Proposal(), CancellationToken.None);

		Assert.Single(inner.Presented);                  // the inner review was consulted
		Assert.Equal(DiffResult.Rejected, outcome.Result);
	}

	[Fact]
	public async Task AcceptEdits_AutoKeepsWithoutConsultingInner() {
		var inner = FakeDiffPresenter.AlwaysReject();    // would reject if it were consulted
		var presenter = new PermissionModeDiffPresenter(inner, Mode("acceptEdits"));

		var outcome = await presenter.PresentDiffAsync(Proposal(), CancellationToken.None);

		Assert.Empty(inner.Presented);                   // short-circuited — inner never saw it
		Assert.Equal(DiffResult.Kept, outcome.Result);
		Assert.Equal("new", outcome.FinalContents);
	}

	[Fact]
	public async Task BypassPermissions_AutoKeepsWithoutConsultingInner() {
		var inner = FakeDiffPresenter.AlwaysReject();
		var presenter = new PermissionModeDiffPresenter(inner, Mode("bypassPermissions"));

		var outcome = await presenter.PresentDiffAsync(Proposal(), CancellationToken.None);

		Assert.Empty(inner.Presented);
		Assert.Equal(DiffResult.Kept, outcome.Result);
	}
}
