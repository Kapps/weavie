using Weavie.Core.TestRunning;

namespace Weavie.Core.Configuration;

/// <summary>Registers the per-workspace test-running setting (<see cref="Profile"/>).</summary>
public static class TestSettings {
	/// <summary>The workspace's test profile: a JSON array of rules mapping test files to run commands.</summary>
	public const string Profile = "test.profile";

	/// <summary>Registers <see cref="Profile"/> into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new SettingDefinition {
			Key = Profile,
			Kind = SettingKind.String,
			Description = "This workspace's test profile: a JSON array of rules that map test files to run "
				+ "commands, so Weavie shows run buttons on test blocks without knowing any framework. Each rule is "
				+ "{ \"glob\": file glob, \"symbol\": regex over a symbol name (first capture = test name), "
				+ "\"runOne\": command template, \"runFile\": command template, optional \"nameSeparator\" (joins "
				+ "nested names, default space), optional \"header\": regex over a symbol's attribute/annotation "
				+ "lines }. Templates support ${file}, ${fileDir} (absolute paths) and ${name} (runOne only). The "
				+ "first rule whose glob matches a file wins. Empty means unconfigured (no buttons); [] means this "
				+ "repo has no tests. Stored per-repo in .weavie/settings.toml; set it via 'Set Up This Workspace'.",
			Aliases = ["test profile", "test runner", "how to run tests", "test command", "run tests config",
				"test running", "configure tests"],
			Apply = ApplyMode.Live,
			Scope = SettingScope.Workspace,
			Default = "",
			Validate = TestProfile.Validate,
		});
	}
}
