namespace Weavie.Core.Credentials;

/// <summary>Where Weavie keeps the secrets it holds, one per stable service id (<c>notion</c>, <c>openai</c>, …).</summary>
public interface ICredentialStore {
	/// <summary>The secret saved for <paramref name="id"/>, or null when none is; throws
	/// <see cref="InvalidOperationException"/> when one is present but unreadable.</summary>
	CredentialSecret? Read(string id);

	/// <summary>Saves <paramref name="secret"/> for <paramref name="id"/>, replacing any previous one.</summary>
	void Save(string id, CredentialSecret secret);

	/// <summary>Deletes the secret saved for <paramref name="id"/>; a no-op when none is.</summary>
	void Delete(string id);
}
