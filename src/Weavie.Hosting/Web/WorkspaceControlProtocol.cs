using System.Reflection;

namespace Weavie.Hosting.Web;

/// <summary>The runner↔worker control-plane contract compiled into the shared workspace host.</summary>
public static class WorkspaceControlProtocol {
	/// <summary>The exact spawn-contract generation this worker requires from its runner.</summary>
	public static int SpawnContract { get; } =
		typeof(WorkspaceControlProtocol).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(attribute => attribute.Key == "SpawnContract")?.Value is { } value
			&& int.TryParse(value, out int parsed)
				? parsed
				: throw new InvalidOperationException(
					"Weavie.Hosting has no SpawnContract assembly metadata — the project stamp is missing.");
}
