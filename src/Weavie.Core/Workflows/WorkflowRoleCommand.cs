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
			WeavieWorkflows.RequireProvider(providers, WeavieWorkflows.Required(agent, "provider", "agent.provider")).Id,
			WeavieWorkflows.Required(agent, "model", "agent.model"), WorkflowRolePick.ReadOptions(agent));
		roles.Set(provider, new Dictionary<string, WorkflowRoleAgent> { [role] = saved });
		string options = saved.Options.Count == 0 ? "" : $" ({string.Join(", ", saved.Options.Select(o => $"{o.Key} {o.Value}"))})";
		return $"{role} in {provider} sessions is now {saved.Model}{options}"
			+ (saved.Provider == provider ? "." : $" on {saved.Provider}, which runs only once steps on another provider are built.");
	}
}
