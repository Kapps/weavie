using Weavie.Core.Agents;
using Weavie.Core.Json;

namespace Weavie.Core.Configuration;

/// <summary>Agent settings surfaced live to the web.</summary>
public static class AgentSettings {
	/// <summary>The provider id new sessions default to; the New Session prompt both reads and updates it.</summary>
	public const string DefaultProvider = "agent.defaultProvider";

	/// <summary>How long (ms) to batch a structured pane's live messages into one bridge frame; 0 sends each inline.</summary>
	public const string PaneCoalesceMs = "agent.paneCoalesceMs";

	/// <summary>Automatically allows ACP tool permissions, excluding mode changes such as plan approval.</summary>
	public const string AllowAllPermissions = "agent.allowAllPermissions";

	/// <summary>Opens an agent's plan in the editor as soon as it asks for review.</summary>
	public const string PlanReviewAutoOpen = "agent.planReview.autoOpen";

	/// <summary>Loads the https images an agent's reply shows, fetched from the machine the agent runs on.</summary>
	public const string RemoteImages = "agent.remoteImages";

	/// <summary>How far down the agent pane a reply read from its start rests, as a fraction of the pane's height.</summary>
	public const string ReplyStartOffset = "agent.replyStartOffset";

	/// <summary>Whether thought text is shown inline or in a separate collapsed disclosure.</summary>
	public const string ThoughtDisplay = "agent.thoughtDisplay";

	/// <summary>Whether highlighting reply text attaches it to the next prompt.</summary>
	public const string AttachOnSelect = "agent.attachOnSelect";

	/// <summary>The keys the host subscribes to, to re-push on change.</summary>
	public static readonly IReadOnlyList<string> Keys = [DefaultProvider, CoreSettings.ClaudePath, ReplyStartOffset, ThoughtDisplay, AttachOnSelect];

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
				writer.WriteString("warning", provider.Warning);
				writer.WriteString(
					"surface",
					provider.Capabilities.HasFlag(AgentProviderCapabilities.StructuredPane)
						? "structured"
						: "terminal");
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteNumber("replyStartOffset", store.RequireNumber(ReplyStartOffset));
			writer.WriteString("thoughtDisplay", store.RequireString(ThoughtDisplay));
			writer.WriteBoolean("attachOnSelect", store.RequireBool(AttachOnSelect));
		});
	}
}
