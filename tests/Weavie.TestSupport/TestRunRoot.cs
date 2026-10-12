using Weavie.Core.Processes;

namespace Weavie.TestSupport;

/// <summary>
/// The one directory a test process owns for everything it writes outside the repo. Opening it points the
/// process's (and so every child's) system temp path and <c>WEAVIE_ROOT</c> inside it; closing it fails if any
/// test left temp state behind, and the whole tree is deleted as the process exits.
/// <see cref="LeakCheckedTestFramework"/> drives both.
/// </summary>
public static class TestRunRoot {
	private static Dictionary<string, string?>? _inherited;
	private static string? _root;

	/// <summary>Creates the run root and redirects the temp path and <c>WEAVIE_ROOT</c> into it.</summary>
	public static void Open() {
		if (_root is not null) throw new InvalidOperationException("The test run root is already open.");
		string system = Path.GetTempPath();
		// Short on purpose: macOS caps a Unix socket path at 104 bytes and the pipes the tests open live under it.
		_root = Directory.CreateTempSubdirectory("wvt-").FullName;
		string temp = Directory.CreateDirectory(Path.Combine(_root, "t")).FullName;
		var redirect = ChildTemp.TempVariables.ToDictionary(name => name, string? (_) => temp);
		redirect["WEAVIE_ROOT"] = Path.Combine(_root, "weavie");
		// A run launched from inside an owned child owns its temp itself; its terminals must not escape to the user's.
		redirect[ChildTemp.UserTempVariable] = null;
		// Enables Node's compile cache for every Node child, shared across runs at Node's default location.
		redirect["NODE_COMPILE_CACHE"] = Path.Combine(system, "node-compile-cache");
		_inherited = redirect.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
		foreach (var (name, value) in redirect) Environment.SetEnvironmentVariable(name, value);
	}

	/// <summary>Throws when a test left anything in the temp path; the root itself goes when the process does.</summary>
	public static void Close() {
		string root = _root ?? throw new InvalidOperationException("The test run root is not open.");
		foreach (var (name, value) in _inherited!) Environment.SetEnvironmentVariable(name, value);
		_root = null;
		// The run's own child-temp root is where owned children live; a directory left inside it outlived its child.
		string temp = Path.Combine(root, "t");
		string? children = ChildTemp.RootCreated ? ChildTemp.Root : null;
		string[] leaked = [.. Directory.EnumerateFileSystemEntries(temp)
			.Where(entry => entry != children)
			.Concat(children is null ? [] : Directory.EnumerateDirectories(children))
			.Select(entry => Path.GetRelativePath(temp, entry)).Order(StringComparer.Ordinal)];
		// Process-lifetime state still writes under the root after the last test (the exit journal a host
		// installs stamps how the process ended), and this handler runs after every one registered before it.
		AppDomain.CurrentDomain.ProcessExit += (_, _) => TempDirectory.Delete(root);
		if (leaked.Length > 0) {
			string message =
				$"Tests left {leaked.Length} temp entries behind; each test must remove what it creates: {string.Join(", ", leaked)}";
			// The runner's console logger drops an assembly-cleanup failure's message.
			Console.Error.WriteLine(message);
			throw new InvalidOperationException(message);
		}
	}
}
