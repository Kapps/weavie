using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Native Codex settings accept the documented app-server policy values.</summary>
public sealed class CodexSettingsTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-codex-settings-tests", Guid.NewGuid().ToString("N"));

	public CodexSettingsTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private string FilePath => Path.Combine(_dir, "settings.toml");

	[Fact]
	public void ApprovalPolicy_AcceptsOnFailure() {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		var result = store.Set("codex.approvalPolicy", JsonDocument.Parse("\"on-failure\"").RootElement);

		Assert.True(result.Written);
		Assert.Equal("on-failure", store.Resolve("codex.approvalPolicy").Value);
	}
}
