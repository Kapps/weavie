using System.Text.Json;
using Weavie.Core.Json;

namespace Weavie.Core.Agents.Codex;

/// <summary>One model from Codex's catalog with the reasoning efforts and service tiers it offers.</summary>
public sealed record CodexModelEntry {
	/// <summary>The model itself, as a selectable option for the model axis.</summary>
	public required AgentControlOption Model { get; init; }

	/// <summary>Whether Codex marks this the catalog default (used when no model is configured).</summary>
	public required bool IsDefault { get; init; }

	/// <summary>The effort id Codex uses for this model when none is chosen.</summary>
	public required string DefaultEffort { get; init; }

	/// <summary>The reasoning efforts this model supports, as options for the effort axis.</summary>
	public required IReadOnlyList<AgentControlOption> Efforts { get; init; }

	/// <summary>The service tier id Codex uses for this model when none is chosen, or empty for standard.</summary>
	public required string DefaultServiceTier { get; init; }

	/// <summary>The non-standard service tiers this model offers (e.g. the "Fast" priority tier).</summary>
	public required IReadOnlyList<AgentControlOption> ServiceTiers { get; init; }
}

/// <summary>Reads a Codex <c>model/list</c> result into the per-model catalog that drives the session's controls.</summary>
public static class CodexModelCatalog {
	/// <summary>Parses a <c>model/list</c> result into model entries carrying their efforts and service tiers.</summary>
	public static bool TryReadModelCatalog(JsonElement result, out IReadOnlyList<CodexModelEntry> models) {
		models = [];
		if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) {
			return false;
		}

		List<CodexModelEntry> entries = [];
		foreach (var item in data.EnumerateArray()) {
			string id = item.GetStringOrEmpty("id");
			if (id.Length == 0) {
				continue;
			}

			string label = item.GetStringOrEmpty("displayName");
			string description = item.GetStringOrEmpty("description");
			entries.Add(new CodexModelEntry {
				Model = new AgentControlOption {
					Id = id,
					Label = label.Length > 0 ? label : id,
					Description = description.Length > 0 ? description : null,
				},
				IsDefault = item.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True,
				DefaultEffort = item.GetStringOrEmpty("defaultReasoningEffort"),
				Efforts = ReadEfforts(item),
				DefaultServiceTier = item.GetStringOrEmpty("defaultServiceTier"),
				ServiceTiers = ReadServiceTiers(item),
			});
		}

		models = entries;
		return true;
	}

	private static IReadOnlyList<AgentControlOption> ReadEfforts(JsonElement model) {
		if (!model.TryGetProperty("supportedReasoningEfforts", out var efforts) || efforts.ValueKind != JsonValueKind.Array) {
			return [];
		}

		List<AgentControlOption> options = [];
		foreach (var item in efforts.EnumerateArray()) {
			string effort = item.GetStringOrEmpty("reasoningEffort");
			if (effort.Length == 0) {
				continue;
			}

			string description = item.GetStringOrEmpty("description");
			options.Add(new AgentControlOption {
				Id = effort,
				Label = EffortLabel(effort),
				Description = description.Length > 0 ? description : null,
			});
		}

		return options;
	}

	private static IReadOnlyList<AgentControlOption> ReadServiceTiers(JsonElement model) {
		if (!model.TryGetProperty("serviceTiers", out var tiers) || tiers.ValueKind != JsonValueKind.Array) {
			return [];
		}

		List<AgentControlOption> options = [];
		foreach (var item in tiers.EnumerateArray()) {
			string id = item.GetStringOrEmpty("id");
			if (id.Length == 0) {
				continue;
			}

			string name = item.GetStringOrEmpty("name");
			string description = item.GetStringOrEmpty("description");
			options.Add(new AgentControlOption {
				Id = id,
				Label = name.Length > 0 ? name : id,
				Description = description.Length > 0 ? description : null,
			});
		}

		return options;
	}

	// Unknown efforts still render (capitalized), so a new Codex effort is never dropped for want of a label.
	private static string EffortLabel(string effort) =>
		effort switch {
			"xhigh" => "Extra high",
			_ => char.ToUpperInvariant(effort[0]) + effort[1..],
		};
}
