using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Diffs;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The edit-permission policy: <c>default</c> delegates to the inner presenter (blocking review);
/// <c>acceptEdits</c> auto-keeps without consulting it. The mode is read live from the store.
/// </summary>
public sealed class PermissionModeDiffPresenterTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-permmode-tests", Guid.NewGuid().ToString("N"));

	public PermissionModeDiffPresenterTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private SettingsStore NewStore() => CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);

	private static DiffProposal Proposal() => new("/a.txt", "/a.txt", "new", "a.txt");

	[Fact]
	public async Task Default_DelegatesToInnerPresenter() {
		using var store = NewStore();
		var inner = FakeDiffPresenter.AlwaysReject();
		var presenter = new PermissionModeDiffPresenter(inner, store);

		var outcome = await presenter.PresentDiffAsync(Proposal(), CancellationToken.None);

		Assert.Single(inner.Presented);                  // the inner review was consulted
		Assert.Equal(DiffResult.Rejected, outcome.Result);
	}

	[Fact]
	public async Task AcceptEdits_AutoKeepsWithoutConsultingInner() {
		using var store = NewStore();
		store.Set("claude.permissionMode", JsonSerializer.SerializeToElement("acceptEdits"));
		var inner = FakeDiffPresenter.AlwaysReject();    // would reject if it were consulted
		var presenter = new PermissionModeDiffPresenter(inner, store);

		var outcome = await presenter.PresentDiffAsync(Proposal(), CancellationToken.None);

		Assert.Empty(inner.Presented);                   // short-circuited — inner never saw it
		Assert.Equal(DiffResult.Kept, outcome.Result);
		Assert.Equal("new", outcome.FinalContents);
	}
}
