using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Xunit;

namespace Weavie.Core.Tests.Inference;

[Collection("Settings")]
public sealed class InferenceCommandsTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-inference-command-tests");

	public InferenceCommandsTests() {
		Environment.SetEnvironmentVariable("WEAVIE_INFERENCE_ENABLED", null);
		Environment.SetEnvironmentVariable("WEAVIE_INFERENCE_ALLOWAUTOMATIC", null);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("WEAVIE_INFERENCE_ENABLED", null);
		Environment.SetEnvironmentVariable("WEAVIE_INFERENCE_ALLOWAUTOMATIC", null);
		_dir.Dispose();
	}

	[Fact]
	public async Task EnableAutomatic_SetsBothPolicyGates() {
		var (settings, commands) = Harness();
		using (settings) {
			var result = await commands.InvokeAsync(
				CoreCommands.EnableAutomaticInference,
				null,
				CancellationToken.None);

			Assert.True(result.Ok, result.Error);
			Assert.Equal("Automatic inference enabled.", result.Message);
			Assert.True(settings.RequireBool(InferenceSettings.AllowAutomatic));
			Assert.True(settings.RequireBool(InferenceSettings.Enabled));
		}
	}

	[Fact]
	public async Task EnableAutomatic_IsIdempotent() {
		var (settings, commands) = Harness();
		using (settings) {
			await commands.InvokeAsync(CoreCommands.EnableAutomaticInference, null, CancellationToken.None);

			var result = await commands.InvokeAsync(
				CoreCommands.EnableAutomaticInference,
				null,
				CancellationToken.None);

			Assert.True(result.Ok, result.Error);
			Assert.Contains("already enabled", result.Message, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task EnableAutomatic_ReportsAnEnvironmentShadow() {
		Environment.SetEnvironmentVariable("WEAVIE_INFERENCE_ALLOWAUTOMATIC", "false");
		var (settings, commands) = Harness();
		using (settings) {
			var result = await commands.InvokeAsync(
				CoreCommands.EnableAutomaticInference,
				null,
				CancellationToken.None);

			Assert.False(result.Ok);
			Assert.Contains("WEAVIE_INFERENCE_ALLOWAUTOMATIC", result.Error, StringComparison.Ordinal);
			Assert.False(settings.RequireBool(InferenceSettings.AllowAutomatic));
		}
	}

	[Fact]
	public async Task EnableAutomatic_PersistsAGateAlreadySuppliedByTheEnvironment() {
		Environment.SetEnvironmentVariable("WEAVIE_INFERENCE_ALLOWAUTOMATIC", "true");
		var (settings, commands) = Harness();
		using (settings) {
			var result = await commands.InvokeAsync(
				CoreCommands.EnableAutomaticInference,
				null,
				CancellationToken.None);

			Assert.True(result.Ok, result.Error);
		}

		Environment.SetEnvironmentVariable("WEAVIE_INFERENCE_ALLOWAUTOMATIC", null);
		using var reloaded = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		Assert.True(reloaded.RequireBool(InferenceSettings.AllowAutomatic));
		Assert.True(reloaded.RequireBool(InferenceSettings.Enabled));
	}

	[Fact]
	public void Catalog_AdvertisesTheOptInCommandAndShortcut() {
		var command = CoreCommands.CreateRegistry().Require(CoreCommands.EnableAutomaticInference);

		Assert.Equal(CommandLocation.Core, command.RunsIn);
		Assert.Equal(CommandOwner.Client, command.Owner);
		Assert.Equal("$mod+alt+i", Assert.Single(command.DefaultKeybindings).Key);
		Assert.True(command.ShowInPalette);
	}

	private (SettingsStore Settings, CommandDispatcher Commands) Harness() {
		var settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		var commands = new CommandDispatcher(CoreCommands.CreateRegistry());
		InferenceCommands.RegisterHandlers(commands, settings);
		return (settings, commands);
	}
}
