namespace Weavie.Core.Commands;

/// <summary>Session commands for the bundled Weavie issue-reporting workflows.</summary>
public static class IssueReportingCommands {
	/// <summary>Prepares a bug report for Weavie's public tracker.</summary>
	public const string ReportBug = "weavie.issue.reportBug";

	/// <summary>Prepares a feature request for Weavie's public tracker.</summary>
	public const string RequestFeature = "weavie.issue.requestFeature";

	/// <summary>Registers reporting actions in the shared command catalog.</summary>
	public static void Register(CommandRegistry registry) {
		registry.Register(new CommandDefinition {
			Id = ReportBug,
			Title = "Report a Weavie Bug",
			RunsIn = CommandLocation.Core,
			Category = "Help",
			Description = "Prefill the agent with instructions to prepare a Weavie bug report, excluding sensitive information and private source code. Review the public report before publishing.",
			Aliases = ["report bug", "file issue", "weavie bug report"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+alt+b" }],
		});
		registry.Register(new CommandDefinition {
			Id = RequestFeature,
			Title = "Request a Weavie Feature",
			RunsIn = CommandLocation.Core,
			Category = "Help",
			Description = "Prefill the agent with instructions to prepare a Weavie feature request, excluding sensitive information and private source code. Review the public request before publishing.",
			Aliases = ["request feature", "suggest feature", "weavie feature request"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+alt+f" }],
		});
	}
}
