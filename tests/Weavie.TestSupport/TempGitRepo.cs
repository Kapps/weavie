using System.Diagnostics;

namespace Weavie.TestSupport;

/// <summary>
/// A throwaway git repository: a <see cref="TempDirectory"/> with an initialised repo on <c>main</c> and a
/// committer identity, so tests that must agree with real <c>git</c> stop re-deriving init/commit plumbing.
/// Requires <c>git</c> on PATH.
/// </summary>
public sealed class TempGitRepo : IDisposable {
	/// <summary>Author email on every commit this repo makes.</summary>
	public const string AuthorEmail = "test@weavie.dev";

	/// <summary>Author name on every commit this repo makes.</summary>
	public const string AuthorName = "Weavie Test";

	private readonly TempDirectory _directory;

	/// <summary>Initialises a repo under a generic prefix.</summary>
	public TempGitRepo() : this("weavie-repo") {
	}

	/// <summary>Initialises a repo in a temp directory whose name starts with <paramref name="prefix"/>.</summary>
	public TempGitRepo(string prefix) {
		_directory = new TempDirectory(prefix);
		Init(Path);
	}

	/// <summary>The repository's working-tree root.</summary>
	public string Path => _directory.Path;

	/// <summary>Initialises a repo on <c>main</c> with a committer identity in an existing directory.</summary>
	public static void Init(string workingDirectory) {
		Run(workingDirectory, "init", "--quiet", "-b", "main");
		Run(workingDirectory, "config", "user.email", AuthorEmail);
		Run(workingDirectory, "config", "user.name", AuthorName);
		Run(workingDirectory, "config", "commit.gpgsign", "false");
	}

	/// <summary>Runs <c>git</c> in <paramref name="workingDirectory"/> and returns stdout; throws on a non-zero exit.</summary>
	public static string Run(string workingDirectory, params string[] args) {
		var info = new ProcessStartInfo("git") {
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		foreach (string arg in args) {
			info.ArgumentList.Add(arg);
		}

		using var process = Process.Start(info) ?? throw new InvalidOperationException("git failed to start");
		string output = process.StandardOutput.ReadToEnd();
		string error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return process.ExitCode == 0
			? output
			: throw new InvalidOperationException(
				$"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {error.Trim()}");
	}

	/// <summary>Runs <c>git</c> in this repository and returns stdout.</summary>
	public string Git(params string[] args) => Run(Path, args);

	/// <summary>Writes a working-tree file, creating any missing parents, and returns its path.</summary>
	public string Write(string relativePath, string contents) => _directory.WriteFile(relativePath, contents);

	/// <summary>Stages everything and commits, returning the new commit's sha.</summary>
	public string Commit(string message) {
		Git("add", "-A");
		Git("commit", "--quiet", "-m", message);
		return Head();
	}

	/// <summary>The sha <c>HEAD</c> points at.</summary>
	public string Head() => Git("rev-parse", "HEAD").Trim();

	/// <inheritdoc/>
	public void Dispose() => _directory.Dispose();
}
