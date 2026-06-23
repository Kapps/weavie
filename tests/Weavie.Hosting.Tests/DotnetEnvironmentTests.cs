using Weavie.Hosting;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <see cref="DotnetEnvironment"/> derives <c>DOTNET_ROOT</c> as the <c>dotnet</c> muxer's directory, but only
/// when that directory actually holds an <c>sdk</c> folder — so a stray <c>dotnet</c> on PATH never sets a root
/// that would break a working, self-locating install.
/// </summary>
public sealed class DotnetEnvironmentTests {
	[Fact]
	public void DeriveRoot_ReturnsMuxerDirectory_WhenSdkSibling() =>
		Assert.Equal(
			Path.Combine("opt", "dotnet"),
			DotnetEnvironment.DeriveRoot(
				Path.Combine("opt", "dotnet", "dotnet"),
				dir => dir == Path.Combine("opt", "dotnet", "sdk")));

	[Fact]
	public void DeriveRoot_ReturnsNull_WhenNoSdkSibling() =>
		Assert.Null(DotnetEnvironment.DeriveRoot(
			Path.Combine("usr", "local", "bin", "dotnet"),
			_ => false));
}
