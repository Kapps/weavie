using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Tests for <see cref="RepoRef.FromRemoteUrl"/> across the remote-URL forms git emits.</summary>
public sealed class RepoRefTests {
	[Theory]
	[InlineData("https://github.com/Kapps/weavie.git", "github.com", "Kapps", "weavie")]
	[InlineData("https://github.com/Kapps/weavie", "github.com", "Kapps", "weavie")]
	[InlineData("git@github.com:Kapps/weavie.git", "github.com", "Kapps", "weavie")]
	[InlineData("ssh://git@github.com/Kapps/weavie.git", "github.com", "Kapps", "weavie")]
	[InlineData("https://github.example.com:8443/org/repo.git", "github.example.com", "org", "repo")]
	[InlineData("http://local_proxy@127.0.0.1:41729/git/Kapps/weavie", "127.0.0.1", "Kapps", "weavie")]
	public void FromRemoteUrl_ParsesHostOwnerName(string url, string host, string owner, string name) {
		var repo = RepoRef.FromRemoteUrl(url);

		Assert.NotNull(repo);
		Assert.Equal(host, repo!.Host);
		Assert.Equal(owner, repo.Owner);
		Assert.Equal(name, repo.Name);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("not-a-url")]
	[InlineData("https://github.com/onlyone")]
	public void FromRemoteUrl_ReturnsNullForUnparseable(string? url) =>
		Assert.Null(RepoRef.FromRemoteUrl(url));
}
