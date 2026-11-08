using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

[Collection("Settings")]
public sealed class NumberSettingsTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-number-settings");
	private string FilePath => _dir.Combine("settings.toml");

	[Theory]
	[InlineData("0", 0)]
	[InlineData("1", 1)]
	[InlineData("0.79", 0.79)]
	[InlineData("\"0.91\"", 0.91)]
	public void ConfidenceRoundTripsAsANumberAndNotifies(string json, double expected) {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);
		Assert.Equal(0.8, store.RequireNumber(WorkflowSettings.DecisionConfidence));
		var changes = new List<SettingChange>();
		store.SettingChanged += changes.Add;
		using var input = JsonDocument.Parse(json);
		Assert.Equal(ApplyMode.Live, store.Set(WorkflowSettings.DecisionConfidence, input.RootElement).Apply);
		Assert.Equal(expected, Assert.Single(changes).NewValue);
		using var reloaded = CoreSettings.CreateStore(FilePath, enableWatcher: false);
		Assert.Equal(expected, reloaded.RequireNumber(WorkflowSettings.DecisionConfidence));
		using var catalog = JsonDocument.Parse(reloaded.BuildCatalogJson());
		var setting = catalog.RootElement.GetProperty("settings").EnumerateArray()
			.Single(s => s.GetProperty("key").GetString() == WorkflowSettings.DecisionConfidence);
		Assert.Equal("number", setting.GetProperty("type").GetString());
		Assert.Equal(expected, setting.GetProperty("value").GetDouble());
		Assert.Equal(0.8, setting.GetProperty("default").GetDouble());
	}

	[Theory]
	[InlineData("-0.1")]
	[InlineData("1.1")]
	[InlineData("1e999")]
	[InlineData("\"NaN\"")]
	[InlineData("\"Infinity\"")]
	[InlineData("\"0,8\"")]
	[InlineData("true")]
	[InlineData("null")]
	public void InvalidNumbersAreRejected(string json) {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);
		using var input = JsonDocument.Parse(json);
		Assert.Throws<SettingValidationException>(() => store.Set(WorkflowSettings.DecisionConfidence, input.RootElement));
		Assert.Equal(0.8, store.RequireNumber(WorkflowSettings.DecisionConfidence));
		Assert.False(File.Exists(FilePath));
	}

	[Theory]
	[InlineData("0", 0)]
	[InlineData("1", 1)]
	[InlineData("0.75", 0.75)]
	[InlineData("nan", 0.8)]
	[InlineData("inf", 0.8)]
	[InlineData("-0.1", 0.8)]
	public void TomlNumbersUseExistingInvalidValuePolicy(string value, double expected) {
		File.WriteAllText(FilePath, "workflow.decisionConfidence = " + value);
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);
		Assert.Equal(expected, store.RequireNumber(WorkflowSettings.DecisionConfidence));
	}

	[Fact]
	public void EnvironmentWinsAndInvalidEnvironmentIsReported() {
		const string env = "WEAVIE_WORKFLOW_DECISIONCONFIDENCE";
		string? previous = Environment.GetEnvironmentVariable(env);
		try {
			File.WriteAllText(FilePath, "workflow.decisionConfidence = 0.7");
			Environment.SetEnvironmentVariable(env, "0.9");
			using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);
			Assert.Equal(new ResolvedValue(0.9, SettingSource.Environment), store.Resolve(WorkflowSettings.DecisionConfidence));
			var logs = new List<string>();
			store.Log += logs.Add;
			Environment.SetEnvironmentVariable(env, "NaN");
			Assert.Equal(0.7, store.RequireNumber(WorkflowSettings.DecisionConfidence));
			Assert.Contains(logs, log => log.Contains(env, StringComparison.Ordinal));
		} finally { Environment.SetEnvironmentVariable(env, previous); }
	}

	public void Dispose() => _dir.Dispose();
}
