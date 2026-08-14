using Weavie.Core.Json;

namespace Weavie.Core.Configuration;

/// <summary>Agent settings surfaced live to the web.</summary>
public static class AgentSettings {
	/// <summary>The provider id new sessions default to; the New Session prompt both reads and updates it.</summary>
	public const string DefaultProvider = "agent.defaultProvider";

	/// <summary>How long (ms) to batch a structured pane's live messages into one bridge frame; 0 sends each inline.</summary>
	public const string PaneCoalesceMs = "agent.paneCoalesceMs";

	/// <summary>Linux middle-click autoscroll for the structured-agent transcript.</summary>
	public const string MiddleClickAutoscroll = "linux.agentMiddleClickAutoscroll";

	/// <summary>The keys the host subscribes to, to re-push on change.</summary>
	public static readonly IReadOnlyList<string> Keys = [DefaultProvider, MiddleClickAutoscroll];

	/// <summary>Builds the resolved agent defaults for the web (the bootstrap global or the change push).</summary>
	public static string BuildJson(SettingsStore store) {
		ArgumentNullException.ThrowIfNull(store);
		return JsonWrite.Object(writer => {
			writer.WriteString("defaultProvider", store.RequireString(DefaultProvider));
			writer.WriteBoolean("middleClickAutoscroll", store.RequireBool(MiddleClickAutoscroll));
		});
	}
}
