namespace Weavie.Core.Processes;

/// <summary>Escapes command-processor input redirection shared by Windows launchers.</summary>
public static class WindowsCommandLine {
	/// <summary>Escapes input-redirection markers in one command-processor argument.</summary>
	public static string EscapeInputRedirection(string value) {
		ArgumentNullException.ThrowIfNull(value);
		return value
			.Replace("^", "^^", StringComparison.Ordinal)
			.Replace("<", "^<", StringComparison.Ordinal);
	}
}
