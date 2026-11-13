using System.Text.Json;
using Weavie.Core.Commands;

namespace Weavie.Core.Configuration;

/// <summary>
/// Wires the Core handlers that show or hide each kind of review item. Each writes its setting rather than holding its
/// own flag, so the filter menu, the palette, and an agent changing the setting never disagree.
/// </summary>
public static class ReviewItemCommands {
	/// <summary>The commands' argument: <c>{"show": true}</c> sets the setting; without it the command flips it.</summary>
	public const string ArgsSchemaJson = "{\"show\":{\"type\":\"boolean\",\"description\":\"Show (true) or hide (false); omit to toggle\"}}";

	/// <summary>Registers the review item toggle handlers onto <paramref name="dispatcher"/>.</summary>
	public static void RegisterHandlers(CommandDispatcher dispatcher, SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(settings);
		foreach (var (command, key) in new[] {
			(CoreCommands.PullRequestToggleComments, EditorSettings.ReviewComments),
			(CoreCommands.ReviewHintsToggle, EditorSettings.ReviewHints),
			(CoreCommands.ReviewToggleSettledItems, EditorSettings.ReviewStepSettled),
		}) {
			dispatcher.RegisterHandler(command, (argsJson, _) => Task.FromResult(Toggle(settings, key, argsJson)));
		}
	}

	private static CommandResult Toggle(SettingsStore settings, string key, string? argsJson) {
		bool? show;
		using (var args = JsonDocument.Parse(argsJson ?? "{}")) {
			show = args.RootElement.ValueKind != JsonValueKind.Object || !args.RootElement.TryGetProperty("show", out var value)
				? !settings.RequireBool(key)
				: value.ValueKind switch {
					JsonValueKind.True => true,
					JsonValueKind.False => false,
					_ => null,
				};
		}
		return show is not { } next
			? CommandResult.Failure("'show' must be true or false.")
			: SettingCommand.Set(settings, key, JsonSerializer.SerializeToElement(next, CoreJson.Default.Boolean), next ? "true" : "false");
	}
}
