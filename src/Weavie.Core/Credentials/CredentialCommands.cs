using Weavie.Core.Commands;

namespace Weavie.Core.Credentials;

/// <summary>Declares the two commands that serve every <see cref="CredentialDefinition"/>.</summary>
public static class CredentialCommands {
	private const string ServiceArg =
		"""{"service":{"type":"string","description":"The service id (e.g. notion); omit to pick from the declared services"}}""";

	/// <summary>Registers Connect Service… and Disconnect Service….</summary>
	public static void Register(CommandRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);
		registry.Register(new CommandDefinition {
			Id = CoreCommands.ConnectService,
			Title = "Connect Service…",
			RunsIn = CommandLocation.Core,
			OnTimeout = OperationSeverity.ActionFailed,
			Category = "Credentials",
			Description = "Connect a service Weavie calls with your key, such as Notion. Opens the service's key page and a "
				+ "dialog to paste the key; Weavie checks it before saving it. The agent never sees the key.",
			Aliases = ["connect notion", "sign in", "add api key", "connect service", "authorize", "link account"],
			ArgsSchemaJson = ServiceArg,
		});
		registry.Register(new CommandDefinition {
			Id = CoreCommands.DisconnectService,
			Title = "Disconnect Service…",
			RunsIn = CommandLocation.Core,
			OnTimeout = OperationSeverity.ActionFailed,
			Category = "Credentials",
			Description = "Delete the key Weavie saved for a connected service, such as Notion.",
			Aliases = ["disconnect notion", "sign out", "remove api key", "forget key", "disconnect service"],
			ArgsSchemaJson = ServiceArg,
		});
	}
}
