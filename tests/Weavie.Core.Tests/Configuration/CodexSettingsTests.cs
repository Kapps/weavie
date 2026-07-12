using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Native Codex settings accept exactly the app-server's AskForApproval variants.</summary>
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

	[Theory]
	[InlineData("untrusted")]
	[InlineData("on-request")]
	[InlineData("never")]
	public void ApprovalPolicy_AcceptsAppServerVariants(string policy) {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		var result = store.Set("codex.approvalPolicy", JsonDocument.Parse($"\"{policy}\"").RootElement);

		Assert.True(result.Written);
		Assert.Equal(policy, store.Resolve("codex.approvalPolicy").Value);
	}

	[Fact]
	public void ApprovalPolicy_RejectsRetiredOnFailure() {
		// Codex removed on-failure from AskForApproval; passing it fails every thread/start and thread/resume.
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		Assert.Throws<SettingValidationException>(
			() => store.Set("codex.approvalPolicy", JsonDocument.Parse("\"on-failure\"").RootElement));
		Assert.Equal("on-request", store.Resolve("codex.approvalPolicy").Value);
	}

	[Fact]
	public void ApprovalPolicy_PersistedOnFailure_FallsBackToDefaultOnRead() {
		// A settings.toml written before on-failure was retired must not brick Codex sessions: the invalid
		// value is ignored on read and the default applies.
		File.WriteAllText(FilePath, "codex.approvalPolicy = \"on-failure\"\n");
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		Assert.Equal("on-request", store.Resolve("codex.approvalPolicy").Value);
	}
}
