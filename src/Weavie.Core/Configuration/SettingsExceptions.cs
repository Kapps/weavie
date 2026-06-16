namespace Weavie.Core.Configuration;

/// <summary>Thrown when a setting key is not registered (strict: callers must use exact keys).</summary>
public sealed class UnknownSettingException : Exception {
	/// <summary>Creates the exception for the unregistered <paramref name="key"/>.</summary>
	public UnknownSettingException(string key)
		: base($"Unknown setting '{key}'. Call listSettings for the exact keys.") {
		Key = key;
	}

	/// <summary>Creates the exception with a custom message.</summary>
	public UnknownSettingException(string key, string message) : base(message) {
		Key = key;
	}

	/// <summary>Creates the exception with a custom message and inner cause.</summary>
	public UnknownSettingException(string key, string message, Exception innerException)
		: base(message, innerException) {
		Key = key;
	}

	/// <summary>The offending key.</summary>
	public string Key { get; }
}

/// <summary>Thrown when a value fails coercion to its declared kind or fails validation.</summary>
public sealed class SettingValidationException : Exception {
	/// <summary>Creates the exception for <paramref name="key"/> with the validation <paramref name="message"/>.</summary>
	public SettingValidationException(string key, string message) : base(message) {
		Key = key;
	}

	/// <summary>Creates the exception with a validation message and inner cause.</summary>
	public SettingValidationException(string key, string message, Exception innerException)
		: base(message, innerException) {
		Key = key;
	}

	/// <summary>The setting that failed validation.</summary>
	public string Key { get; }
}

/// <summary>
/// Thrown when a write is refused because <c>settings.toml</c> currently has parse errors — we never
/// clobber an unparseable file the user is mid-edit on.
/// </summary>
public sealed class SettingsFileMalformedException : Exception {
	/// <summary>Creates the exception with the parse-failure <paramref name="message"/>.</summary>
	public SettingsFileMalformedException(string message) : base(message) {
	}

	/// <summary>Creates the exception with a message and inner cause.</summary>
	public SettingsFileMalformedException(string message, Exception innerException)
		: base(message, innerException) {
	}
}
