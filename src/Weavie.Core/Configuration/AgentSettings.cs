using Weavie.Core.Agents;
using Weavie.Core.Json;

namespace Weavie.Core.Configuration;

/// <summary>Agent settings surfaced live to the web.</summary>
public static class AgentSettings {
	/// <summary>The provider id new sessions default to; the New Session prompt both reads and updates it.</summary>
	public const string DefaultProvider = "agent.defaultProvider";

	/// <summary>How long (ms) to batch a structured pane's live messages into one bridge frame; 0 sends each inline.</summary>
	public const string PaneCoalesceMs = "agent.paneCoalesceMs";

	/// <summary>Automatically selects an advertised allow option for ACP permission requests.</summary>
	public const string AllowAllPermissions = "agent.allowAllPermissions";

	/// <summary>The keys the host subscribes to, to re-push on change.</summary>
	public static readonly IReadOnlyList<string> Keys = [DefaultProvider];

	/// <summary>Builds the resolved agent defaults for the web (the bootstrap global or the change push).</summary>
	public static string BuildJson(SettingsStore store, IReadOnlyList<AgentProviderInfo> providers) {
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(providers);
		return JsonWrite.Object(writer => {
			writer.WriteString("defaultProvider", store.RequireString(DefaultProvider));
			writer.WriteStartArray("providers");
			foreach (var provider in providers) {
				writer.WriteStartObject();
				writer.WriteString("id", provider.Id);
				writer.WriteString("name", provider.Name);
				writer.WriteBoolean("available", provider.Available);
				writer.WriteString("unavailableReason", provider.UnavailableReason);
				writer.WriteString(
					"surface",
					provider.Capabilities.HasFlag(AgentProviderCapabilities.StructuredPane)
						? "structured"
						: "terminal");
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
		});
	}
}
