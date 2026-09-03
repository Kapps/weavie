namespace Weavie.Hosting.Desktop;

/// <summary>What a launch was asked to do: the paths to open, and the named options that preceded them.</summary>
/// <param name="Paths">Absolute paths the OS or shell handed over, in order.</param>
/// <param name="Options">Named <c>--option value</c> pairs.</param>
public sealed record LaunchArguments(IReadOnlyList<string> Paths, IReadOnlyDictionary<string, string> Options) {
	/// <summary>A launch with nothing to open and no options.</summary>
	public static LaunchArguments Empty { get; } = new([], new Dictionary<string, string>(StringComparer.Ordinal));

	/// <summary>
	/// Reads <paramref name="args"/> as <c>--option value</c> pairs plus bare operands. An operand may be a
	/// path or a <c>file://</c> URI, because a desktop entry's <c>%U</c> field code hands over URIs; both
	/// become absolute paths, resolved against the current directory.
	/// </summary>
	public static LaunchArguments Parse(IReadOnlyList<string> args) {
		ArgumentNullException.ThrowIfNull(args);
		List<string> paths = [];
		Dictionary<string, string> options = new(StringComparer.Ordinal);
		for (int index = 0; index < args.Count; index++) {
			string argument = args[index];
			if (argument.StartsWith("--", StringComparison.Ordinal)) {
				if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal)) {
					options[argument[2..]] = args[++index];
				} else {
					options[argument[2..]] = string.Empty;
				}
			} else if (ToPath(argument) is { } path) {
				paths.Add(path);
			}
		}

		return new LaunchArguments(paths, options);
	}

	/// <summary>The value of <c>--<paramref name="name"/></c>, or null when it was not given.</summary>
	public string? Option(string name) => Options.TryGetValue(name, out string? value) ? value : null;

	private static string? ToPath(string operand) {
		if (operand.Length == 0) {
			return null;
		}

		try {
			return Uri.TryCreate(operand, UriKind.Absolute, out var uri) && uri.IsFile
				? uri.LocalPath
				: Path.GetFullPath(operand);
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return null;
		}
	}
}
