using Weavie.Core.Json;

namespace Weavie.Core.Configuration;

/// <summary>
/// The agent-provider default surfaced to the web. The New Session prompt reads it to preselect a provider,
/// and creating a session with a different one writes it back — so the dropdown's default tracks the last
/// choice. <see cref="ApplyMode.NextSession"/>: a change re-pushes the resolved value to the page.
/// </summary>
public static class AgentSettings {
	/// <summary>The provider id new sessions default to; the New Session prompt both reads and updates it.</summary>
	public const string DefaultProvider = "agent.defaultProvider";

	/// <summary>The keys the host subscribes to, to re-push on change.</summary>
	public static readonly IReadOnlyList<string> Keys = [DefaultProvider];

	/// <summary>Builds the resolved agent defaults for the web (the bootstrap global or the change push).</summary>
	public static string BuildJson(SettingsStore store, string? messageType) {
		ArgumentNullException.ThrowIfNull(store);
		return JsonWrite.Object(writer => {
			if (messageType is not null) {
				writer.WriteString("type", messageType);
			}

			writer.WriteString("defaultProvider", store.RequireString(DefaultProvider));
		});
	}
}
