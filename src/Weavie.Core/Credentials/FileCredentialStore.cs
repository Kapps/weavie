using Weavie.Core.FileSystem;

namespace Weavie.Core.Credentials;

/// <summary>An <see cref="ICredentialStore"/> keeping one owner-only plain-text file per id: <c>&lt;directory&gt;/&lt;id&gt;.key</c>.</summary>
public sealed class FileCredentialStore : ICredentialStore {
	private readonly string _directory;

	/// <summary>Stores secrets under <paramref name="directory"/>, created owner-only on first save.</summary>
	public FileCredentialStore(string directory) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		_directory = directory;
	}

	/// <inheritdoc/>
	public CredentialSecret? Read(string id) {
		string path = PathFor(id);
		string text;
		try {
			text = File.ReadAllText(path).Trim();
		} catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
			return null;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			throw new InvalidOperationException($"Couldn't read the {id} credential at {path}: {ex.Message}", ex);
		}

		return text.Length > 0 ? new CredentialSecret(text) : throw new InvalidOperationException($"The {id} credential at {path} is empty.");
	}

	/// <inheritdoc/>
	public void Save(string id, CredentialSecret secret) {
		ArgumentNullException.ThrowIfNull(secret);
		string path = PathFor(id);
		SecureFile.CreateDirectory(_directory);
		string temp = $"{path}.{Guid.NewGuid():n}.tmp";
		SecureFile.WriteAllText(temp, secret.Reveal());
		File.Move(temp, path, overwrite: true);
	}

	/// <inheritdoc/>
	public bool Delete(string id) {
		string path = PathFor(id);
		if (!File.Exists(path)) {
			return false;
		}

		File.Delete(path);
		return true;
	}

	private string PathFor(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		if (!id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) {
			throw new ArgumentException($"Credential id '{id}' must be lowercase letters, digits, or dashes.", nameof(id));
		}

		return Path.Combine(_directory, $"{id}.key");
	}
}
