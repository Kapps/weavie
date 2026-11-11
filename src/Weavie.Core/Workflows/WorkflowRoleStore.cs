using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>What fills a role in one provider's sessions: provider-native ids, never inferred.</summary>
/// <param name="Provider">The provider that runs the role's steps.</param>
/// <param name="Model">The provider's model id.</param>
/// <param name="Options">The provider's config option values by option id.</param>
public sealed record WorkflowRoleAgent(string Provider, string Model, IReadOnlyDictionary<string, string> Options) {
	/// <summary>This agent as the engine's agent named <paramref name="role"/>.</summary>
	public WorkflowAgent For(string role) => new() { Name = role, Provider = Provider, Model = Model, Options = Options };
}

/// <summary>One saved role.</summary>
/// <param name="Role">The role name.</param>
/// <param name="Provider">The session provider it is for.</param>
/// <param name="Agent">What fills it there.</param>
public sealed record WorkflowRoleAssignment(string Role, string Provider, WorkflowRoleAgent Agent);

/// <summary>
/// The user's workflow roles: per session provider, the agent that fills each role. An unusable file is never reset,
/// so a role the user chose is never silently lost. See docs/specs/workflow-roles/design.md.
/// </summary>
public sealed partial class WorkflowRoleStore : StrictJsonDocumentStore<Dictionary<string, Dictionary<string, WorkflowRoleAgent>>> {
	private const int Version = 1;

	/// <summary>Creates and loads the store at <paramref name="path"/>.</summary>
	/// <param name="fileSystem">The filesystem the roles persist through.</param>
	/// <param name="path">The backing file.</param>
	public WorkflowRoleStore(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <inheritdoc/>
	protected override string Subject => "Workflow roles";

	/// <summary>What fills <paramref name="role"/> in <paramref name="providerId"/> sessions, or <see langword="null"/>.</summary>
	public WorkflowRoleAgent? Find(string providerId, string role) {
		lock (Gate) return Current.TryGetValue(providerId, out var roles) && roles.TryGetValue(role, out var agent) ? agent : null;
	}

	/// <summary>Every saved role, by provider then role.</summary>
	public IReadOnlyList<WorkflowRoleAssignment> All() {
		lock (Gate) {
			return [.. Current.OrderBy(p => p.Key, StringComparer.Ordinal)
				.SelectMany(p => p.Value.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new WorkflowRoleAssignment(r.Key, p.Key, r.Value)))];
		}
	}

	/// <summary>
	/// Saves each of <paramref name="agents"/> as its role in <paramref name="providerId"/> sessions, all or none; throws
	/// <see cref="WorkflowException"/> for an entry the file couldn't be read back with.
	/// </summary>
	public void Set(string providerId, IReadOnlyDictionary<string, WorkflowRoleAgent> agents) {
		ArgumentNullException.ThrowIfNull(agents);
		if (Problem(providerId, agents) is { } problem) throw new WorkflowException(problem);
		lock (Gate) {
			var next = Clone();
			if (!next.TryGetValue(providerId, out var roles)) next.Add(providerId, roles = new(StringComparer.Ordinal));
			foreach (var (role, agent) in agents) roles[role] = agent;
			Commit(next);
		}
	}

	/// <summary>
	/// Removes <paramref name="role"/> from <paramref name="providerId"/> sessions; false when none was saved. Throws
	/// <see cref="WorkflowException"/> for a name no role can have.
	/// </summary>
	public bool Clear(string providerId, string role) {
		if (!WorkflowSchema.IsRoleName(role)) throw new WorkflowException(NotARoleName(role));
		lock (Gate) {
			var next = Clone();
			if (!next.TryGetValue(providerId, out var roles) || !roles.Remove(role)) return false;
			if (roles.Count == 0) next.Remove(providerId);
			Commit(next);
			return true;
		}
	}

	/// <inheritdoc/>
	protected override Dictionary<string, Dictionary<string, WorkflowRoleAgent>> Empty() => new(StringComparer.Ordinal);

	/// <inheritdoc/>
	protected override Dictionary<string, Dictionary<string, WorkflowRoleAgent>> Read(string text) {
		var document = JsonSerializer.Deserialize(text, WorkflowRoleJson.Default.Document) ?? throw new JsonException("The workflow role document is empty.");
		if (document.Version != Version) throw new JsonException($"The workflow role document is version {document.Version}; this build reads version {Version}.");
		var providers = Empty();
		foreach (var (provider, roles) in document.Providers) {
			if (Problem(provider, roles) is { } problem) throw new JsonException(problem);
			providers.Add(provider, new Dictionary<string, WorkflowRoleAgent>(roles, StringComparer.Ordinal));
		}
		return providers;
	}

	// The one rule for an entry, so nothing is saved that the file can't be read back with.
	private static string? Problem(string providerId, IReadOnlyDictionary<string, WorkflowRoleAgent> agents) =>
		providerId.Length == 0 ? "A workflow role needs a session provider."
		: agents.FirstOrDefault(entry => !WorkflowSchema.IsRoleName(entry.Key)) is { Key: { } name } ? NotARoleName(name)
		: agents.Values.Any(agent => agent.Provider.Length == 0 || agent.Model.Length == 0)
			? "A workflow role needs an agent provider and model."
		: null;

	private static string NotARoleName(string name) => $"\"{name}\" isn't a role name: use lowercase words joined by hyphens.";

	/// <inheritdoc/>
	protected override string Write(Dictionary<string, Dictionary<string, WorkflowRoleAgent>> state) =>
		JsonSerializer.Serialize(new Document { Version = Version, Providers = state }, WorkflowRoleJson.Default.Document);

	/// <inheritdoc/>
	protected override Exception Failure(string message, Exception cause) => new WorkflowException(message);

	private Dictionary<string, Dictionary<string, WorkflowRoleAgent>> Clone() =>
		Current.ToDictionary(p => p.Key, p => new Dictionary<string, WorkflowRoleAgent>(p.Value, StringComparer.Ordinal), StringComparer.Ordinal);

	private sealed class Document {
		public required int Version { get; set; }

		public required Dictionary<string, Dictionary<string, WorkflowRoleAgent>> Providers { get; set; }
	}

	[JsonSourceGenerationOptions(
		WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
	[JsonSerializable(typeof(Document))]
	private sealed partial class WorkflowRoleJson : JsonSerializerContext;
}
