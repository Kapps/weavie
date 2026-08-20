using System.Text.Json;
using Weavie.AcpDistribution;
using Weavie.AgentClientProtocol;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Weavie.Hosting.Agents.Claude;

namespace Weavie.Hosting.Agents;

/// <summary>Builds the one authoritative provider catalog shared by every platform host.</summary>
public static class AgentProviderComposition {
	/// <summary>Creates the terminal Claude provider and every installed ACP provider.</summary>
	public static AgentProviderRegistry Create(
		SettingsStore settings,
		ClaudeSessionStore claudeSessions,
		IAcpAgentCatalog acpAgents) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(claudeSessions);
		ArgumentNullException.ThrowIfNull(acpAgents);
		var providers = new AgentProviderRegistry();
		var claude = new ClaudeAgentProvider(settings, claudeSessions);
		var sessions = new AcpSessionStore(new LocalFileSystem(), Weavie.Core.WeaviePaths.AcpSessionsFile);
		var controls = new AcpControlStore(new LocalFileSystem(), Weavie.Core.WeaviePaths.AcpControlsFile);
		void Rebuild() => providers.ReplaceAll([claude, .. BuildAcpProviders(acpAgents, sessions, controls)]);
		Rebuild();
		acpAgents.Changed += Rebuild;
		return providers;
	}

	private static IReadOnlyList<IAgentProvider> BuildAcpProviders(
		IAcpAgentCatalog catalog,
		AcpSessionStore sessions,
		AcpControlStore controls) {
		IReadOnlyList<AcpLaunchSpec> launches;
		try {
			launches = catalog.LaunchSpecs;
		} catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
			or InvalidDataException) {
			return [new UnavailableProvider("acp-config", "ACP agents", ex.Message)];
		}
		var providers = new List<IAgentProvider>(launches.Count);
		foreach (var launch in launches) {
			if (launch.Id == "claude") {
				return [new UnavailableProvider(
					"acp-config",
					"ACP agents",
					"The ACP provider id 'claude' is reserved by the terminal provider.")];
			}
			providers.Add(new AcpAgentProvider(new AcpAgentDefinition {
				Id = launch.Id,
				Name = launch.Name,
				Command = launch.Command,
				Arguments = launch.Arguments,
				Environment = launch.Environment,
				Distribution = launch.Distribution,
			}, sessions, controls, Console.WriteLine));
		}
		return providers;
	}

	private sealed class UnavailableProvider : IAgentProvider {
		public UnavailableProvider(string id, string name, string reason) {
			Info = new AgentProviderInfo {
				Id = id,
				Name = name,
				Capabilities = AgentProviderCapabilities.StructuredPane,
				Available = false,
				UnavailableReason = reason,
			};
		}

		public AgentProviderInfo Info { get; }

		public IAgentSession CreateSession(AgentSessionContext context) =>
			throw new InvalidOperationException(Info.UnavailableReason);
	}
}
