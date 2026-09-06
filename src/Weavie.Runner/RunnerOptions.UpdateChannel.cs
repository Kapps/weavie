namespace Weavie.Runner;

/// <summary>The release feed the runner follows.</summary>
public enum UpdateChannel {
	/// <summary>Updates are disabled.</summary>
	Off,
	/// <summary>Manually published, versioned releases.</summary>
	Stable,
	/// <summary>Development builds from green main commits.</summary>
	Latest,
}

public sealed partial record RunnerOptions {
	private static (UpdateChannel Channel, string? Error) ResolveUpdateChannel(string[] args) {
		int index = Array.IndexOf(args, "--auto-update");
		if (index < 0) {
			return (UpdateChannel.Off, null);
		}

		if (Array.LastIndexOf(args, "--auto-update") != index) {
			return (UpdateChannel.Off, "Pass --auto-update only once (stable or latest).");
		}

		string value = index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)
			? "stable"
			: args[index + 1];
		return value switch {
			"stable" => (UpdateChannel.Stable, null),
			"latest" => (UpdateChannel.Latest, null),
			_ => (UpdateChannel.Off, $"--auto-update '{value}' is not a known channel (use stable or latest)."),
		};
	}
}
