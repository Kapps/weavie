using System.Globalization;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary><see cref="CoreCommands.WorkflowSetRole"/>: reads its arguments, saves or clears the role, and words the result.</summary>
public static class WorkflowRoleCommand {
	/// <summary>Applies <paramref name="args"/> to <paramref name="roles"/>; throws <see cref="WorkflowException"/> on any bad argument.</summary>
	public static string Run(JsonElement args, WorkflowRoleStore roles, AgentProviderRegistry providers) {
		ArgumentNullException.ThrowIfNull(roles);
		ArgumentNullException.ThrowIfNull(providers);
		string role = WeavieWorkflows.Required(args, "role");
		if (!WorkflowSchema.IsRoleName(role)) {
			throw new WorkflowException($"\"{role}\" isn't a role name: use lowercase words joined by hyphens.");
		}
		string provider = WeavieWorkflows.RequireProvider(providers, WeavieWorkflows.Required(args, "provider")).Id;
		bool clear = args.TryGetProperty("clear", out var clearing) && clearing.ValueKind == JsonValueKind.True;
		bool set = args.TryGetProperty("agent", out var agent);
		if (clear == set) {
			throw new WorkflowException($"{CoreCommands.WorkflowSetRole} takes exactly one of 'agent' or 'clear'.");
		}

		if (clear) {
			return roles.Clear(provider, role)
				? $"Cleared {role} for {provider} sessions."
				: throw new WorkflowException($"No {role} is saved for {provider} sessions.");
		}

		var saved = new WorkflowRoleAgent(
			WeavieWorkflows.RequireProvider(providers, AgentArgument(agent, "provider")).Id, AgentArgument(agent, "model"), Options(agent));
		roles.Set(provider, new Dictionary<string, WorkflowRoleAgent> { [role] = saved });
		string options = saved.Options.Count == 0 ? "" : $" ({string.Join(", ", saved.Options.Select(o => $"{o.Key} {o.Value}"))})";
		return $"{role} in {provider} sessions is now {saved.Model}{options}"
			+ (saved.Provider == provider ? "." : $" on {saved.Provider}, which runs only once steps on another provider are built.");
	}

	private static string AgentArgument(JsonElement agent, string name) =>
		agent.ValueKind == JsonValueKind.Object && agent.TryGetProperty(name, out var value)
			&& value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
			? text
			: throw new WorkflowException("'agent' needs 'provider' and 'model'.");

	// Agents write option values as JSON text, booleans, or numbers; the provider's value ids are text.
	private static Dictionary<string, string> Options(JsonElement agent) {
		var options = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!agent.TryGetProperty("options", out var given)) return options;
		if (given.ValueKind != JsonValueKind.Object) throw new WorkflowException("'agent.options' must be an object of option ids to value ids.");
		foreach (var option in given.EnumerateObject()) {
			options[option.Name] = option.Value.ValueKind switch {
				JsonValueKind.String when option.Value.GetString() is { Length: > 0 } text => text,
				JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number => option.Value.GetRawText().ToLower(CultureInfo.InvariantCulture),
				_ => throw new WorkflowException($"Option '{option.Name}' needs a value id."),
			};
		}
		return options;
	}
}
