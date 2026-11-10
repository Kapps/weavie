namespace Weavie.Core.Credentials;

/// <summary>One service Weavie holds a key for, declared once by the feature that calls it.</summary>
public sealed record CredentialDefinition {
	/// <summary>The stable store id (e.g. <c>notion</c>).</summary>
	public required string Id { get; init; }

	/// <summary>The service's name as the user knows it (e.g. <c>Notion</c>).</summary>
	public required string DisplayName { get; init; }

	/// <summary>What the service calls the key (e.g. <c>personal access token</c>).</summary>
	public required string KeyNoun { get; init; }

	/// <summary>The vendor page where the user creates a key.</summary>
	public required string KeyPageUrl { get; init; }

	/// <summary>Checks a key with one cheap authenticated call and returns the account it authorizes (may be empty);
	/// throws <see cref="InvalidOperationException"/> when the service rejects it.</summary>
	public required Func<CredentialSecret, CancellationToken, Task<string>> ValidateAsync { get; init; }
}
