namespace Weavie.TestSupport;

/// <summary>
/// The one directory a test process owns for everything it writes outside the repo. Opening it points the
/// process's (and so every child's) system temp path and <c>WEAVIE_ROOT</c> inside it; closing it fails if any
/// test left temp state behind, and the whole tree is deleted as the process exits.
/// <see cref="LeakCheckedTestFramework"/> drives both.
/// </summary>
public static class TestRunRoot {
	private static readonly string[] TempVariables = OperatingSystem.IsWindows() ? ["TMP", "TEMP"] : ["TMPDIR"];
	private static Dictionary<string, string?>? _inherited;
	private static string? _root;

	/// <summary>Creates the run root and redirects the temp path and <c>WEAVIE_ROOT</c> into it.</summary>
	public static void Open() {
		if (_root is not null) throw new InvalidOperationException("The test run root is already open.");
		// Short on purpose: macOS caps a Unix socket path at 104 bytes and the pipes the tests open live under it.
		_root = Directory.CreateTempSubdirectory("wvt-").FullName;
		string temp = Directory.CreateDirectory(Path.Combine(_root, "t")).FullName;
		var redirect = TempVariables.ToDictionary(name => name, string? (_) => temp);
		redirect["WEAVIE_ROOT"] = Path.Combine(_root, "weavie");
		// Children never write process-global state into it: Weavie stops agents and LSP servers with SIGKILL,
		// which strands a .NET child's runtime IPC files, and npm keeps a shared compile cache in the temp path.
		redirect["DOTNET_EnableDiagnostics"] = "0";
		redirect["NODE_DISABLE_COMPILE_CACHE"] = "1";
		_inherited = redirect.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
		foreach (var (name, value) in redirect) Environment.SetEnvironmentVariable(name, value);
	}

	/// <summary>Throws when a test left anything in the temp path; the root itself goes when the process does.</summary>
	public static void Close() {
		string root = _root ?? throw new InvalidOperationException("The test run root is not open.");
		foreach (var (name, value) in _inherited!) Environment.SetEnvironmentVariable(name, value);
		_root = null;
		string[] leaked = [.. Directory.EnumerateFileSystemEntries(Path.Combine(root, "t"))
			.Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal)];
		// Process-lifetime state still writes under the root after the last test (the exit journal a host
		// installs stamps how the process ended), and this handler runs after every one registered before it.
		AppDomain.CurrentDomain.ProcessExit += (_, _) => TempDirectory.Delete(root);
		if (leaked.Length > 0) {
			throw new InvalidOperationException(
				$"Tests left {leaked.Length} temp entries behind; each test must remove what it creates: {string.Join(", ", leaked)}");
		}
	}
}
