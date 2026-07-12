using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Native Codex settings accept only the app-server policy values current Codex still ships.</summary>
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
	public void ApprovalPolicy_AcceptsOnRequest() {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		var result = store.Set("codex.approvalPolicy", JsonDocument.Parse("\"untrusted\"").RootElement);

		Assert.True(result.Written);
		Assert.Equal("untrusted", store.Resolve("codex.approvalPolicy").Value);
	}

	[Fact]
	public void ApprovalPolicy_RejectsPolicyRemovedFromCodex() {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		// "on-failure" was deleted in Codex 0.143; sending it to a current app-server fails the request.
		Assert.Throws<SettingValidationException>(
			() => store.Set("codex.approvalPolicy", JsonDocument.Parse("\"on-failure\"").RootElement));
		Assert.Equal("on-request", store.Resolve("codex.approvalPolicy").Value);
	}
}
