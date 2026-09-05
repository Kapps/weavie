using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="ExecutableFinder.FindOnPath"/>: a qualified (rooted / separator-bearing) name resolves to its
/// full path when it exists on disk and is null when it doesn't, and a bare name is found on <c>PATH</c>.
/// </summary>
public sealed class ExecutableFinderTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-exe-finder-tests");

	public void Dispose() => _dir.Dispose();

	[Fact]
	public void QualifiedName_Existing_ResolvesToFullPath() {
		string file = _dir.WriteFile("tool", "");

		Assert.Equal(Path.GetFullPath(file), ExecutableFinder.FindOnPath(file));
	}

	[Fact]
	public void QualifiedName_Missing_ReturnsNull() {
		// A path-qualified name that does not exist must not resolve (no PATH search for a qualified name).
		string missing = _dir.Combine("subdir", "absent");
		Assert.Null(ExecutableFinder.FindOnPath(missing));
	}

	[Fact]
	public void BareName_FoundOnPath() {
		string file = _dir.WriteFile("barecmd", "");
		string old = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		Environment.SetEnvironmentVariable("PATH", _dir.Path + Path.PathSeparator + old);
		try {
			Assert.Equal(file, ExecutableFinder.FindOnPath("barecmd"));
		} finally {
			Environment.SetEnvironmentVariable("PATH", old);
		}
	}
}
