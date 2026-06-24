using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="ExecutableFinder.FindOnPath"/>: a qualified (rooted / separator-bearing) name resolves to its
/// full path when it exists on disk and is null when it doesn't, and a bare name is found on <c>PATH</c>.
/// </summary>
public sealed class ExecutableFinderTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-exe-finder-tests", Guid.NewGuid().ToString("N"));

	public ExecutableFinderTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	[Fact]
	public void QualifiedName_Existing_ResolvesToFullPath() {
		string file = Path.Combine(_dir, "tool");
		File.WriteAllText(file, "");

		Assert.Equal(Path.GetFullPath(file), ExecutableFinder.FindOnPath(file));
	}

	[Fact]
	public void QualifiedName_Missing_ReturnsNull() {
		// A path-qualified name that does not exist must not resolve (no PATH search for a qualified name).
		string missing = Path.Combine(_dir, "subdir", "absent");
		Assert.Null(ExecutableFinder.FindOnPath(missing));
	}

	[Fact]
	public void BareName_FoundOnPath() {
		string file = Path.Combine(_dir, "barecmd");
		File.WriteAllText(file, "");
		string old = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		Environment.SetEnvironmentVariable("PATH", _dir + Path.PathSeparator + old);
		try {
			Assert.Equal(file, ExecutableFinder.FindOnPath("barecmd"));
		} finally {
			Environment.SetEnvironmentVariable("PATH", old);
		}
	}
}
