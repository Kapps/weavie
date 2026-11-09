namespace Weavie.Core.Credentials;

/// <summary>The declared services and their saved keys: connect validates before saving, and every change raises <see cref="Changed"/>.</summary>
public sealed class CredentialService {
	private readonly ICredentialStore _store;

	/// <summary>Serves <paramref name="definitions"/> over <paramref name="store"/>.</summary>
	public CredentialService(IReadOnlyList<CredentialDefinition> definitions, ICredentialStore store) {
		ArgumentNullException.ThrowIfNull(definitions);
		ArgumentNullException.ThrowIfNull(store);
		Definitions = definitions;
		_store = store;
	}

	/// <summary>Raised after a key is saved or deleted.</summary>
	public event Action? Changed;

	/// <summary>Every service Weavie can hold a key for.</summary>
	public IReadOnlyList<CredentialDefinition> Definitions { get; }

	/// <summary>The definition for <paramref name="id"/>; throws <see cref="InvalidOperationException"/> when none is declared.</summary>
	public CredentialDefinition Require(string id) =>
		Definitions.FirstOrDefault(definition => definition.Id == id)
			?? throw new InvalidOperationException($"No service named '{id}' — use one of {string.Join(", ", Definitions.Select(d => d.Id))}.");

	/// <summary>The saved key for <paramref name="id"/>, or null when none is; throws when it is unreadable.</summary>
	public CredentialSecret? Read(string id) => _store.Read(Require(id).Id);

	/// <summary>True when <paramref name="id"/> has a readable saved key; an unreadable one counts as not connected so
	/// the user is sent to reconnect, which overwrites it.</summary>
	public bool IsConnected(string id) {
		try {
			return Read(id) is not null;
		} catch (InvalidOperationException) {
			return false;
		}
	}

	/// <summary>Validates <paramref name="key"/> for <paramref name="id"/> and saves it only when accepted; returns the
	/// account label the service reported.</summary>
	public async Task<string> ConnectAsync(string id, CredentialSecret key, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(key);
		var definition = Require(id);
		string account = await definition.ValidateAsync(key, ct).ConfigureAwait(false);
		_store.Save(definition.Id, key);
		Changed?.Invoke();
		return account;
	}

	/// <summary>Deletes the saved key for <paramref name="id"/>, even an unreadable one; false when none was saved.</summary>
	public bool Disconnect(string id) {
		if (!_store.Delete(Require(id).Id)) {
			return false;
		}

		Changed?.Invoke();
		return true;
	}
}
