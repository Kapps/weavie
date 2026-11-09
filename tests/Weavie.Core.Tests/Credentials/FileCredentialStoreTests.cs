using Weavie.Core.Credentials;
using Xunit;

namespace Weavie.Core.Tests.Credentials;

public sealed class FileCredentialStoreTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-credentials");
	private readonly FileCredentialStore _store;

	public FileCredentialStoreTests() {
		_store = new FileCredentialStore(_dir.Combine("credentials"));
	}

	public void Dispose() => _dir.Dispose();

	[Fact]
	public void SavedSecretRoundTripsAndReplacesThePreviousOne() {
		_store.Save("openai", new CredentialSecret("sk-one"));
		_store.Save("openai", new CredentialSecret("sk-two"));

		Assert.Equal("sk-two", _store.Read("openai")!.Reveal());
		Assert.Equal("sk-two", File.ReadAllText(_dir.Combine("credentials", "openai.key")));
		Assert.Single(Directory.GetFiles(_dir.Combine("credentials")));
	}

	[Fact]
	public void SavedSecretIsOwnerOnly() {
		_store.Save("notion", new CredentialSecret("ntn_x"));

		if (!OperatingSystem.IsWindows()) {
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_dir.Combine("credentials", "notion.key")));
			Assert.Equal(
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
				File.GetUnixFileMode(_dir.Combine("credentials")));
		}
	}

	[Fact]
	public void AbsentSecretReadsAsNull() => Assert.Null(_store.Read("notion"));

	[Fact]
	public void EmptySecretFileThrows() {
		Directory.CreateDirectory(_dir.Combine("credentials"));
		File.WriteAllText(_dir.Combine("credentials", "notion.key"), "  \n");

		Assert.Throws<InvalidOperationException>(() => _store.Read("notion"));
	}

	[Fact]
	public void UnreadableSecretThrows() {
		Directory.CreateDirectory(_dir.Combine("credentials", "notion.key"));

		Assert.Throws<InvalidOperationException>(() => _store.Read("notion"));
	}

	[Fact]
	public void DeleteRemovesTheSecretAndToleratesAbsence() {
		_store.Delete("notion");
		_store.Save("notion", new CredentialSecret("ntn_x"));

		_store.Delete("notion");

		Assert.Null(_store.Read("notion"));
	}

	[Theory]
	[InlineData("Notion")]
	[InlineData("../notion")]
	[InlineData("notion.key")]
	[InlineData("")]
	public void RejectsAnIdThatIsNotAPlainServiceName(string id) =>
		Assert.ThrowsAny<ArgumentException>(() => _store.Read(id));
}
