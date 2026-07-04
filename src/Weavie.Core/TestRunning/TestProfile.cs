using System.Text.Json;
using System.Text.RegularExpressions;
using Weavie.Core.Configuration;

namespace Weavie.Core.TestRunning;

/// <summary>
/// One rule in a workspace's test profile: a file <see cref="Glob"/> plus a <see cref="Symbol"/> regex that
/// together select test blocks from LSP document symbols, and the <see cref="RunOne"/> / <see cref="RunFile"/>
/// command templates that run them. Purely workspace data — Weavie ships no framework knowledge.
/// </summary>
public sealed record TestRule {
	/// <summary>Glob matched against the workspace-relative file path (e.g. <c>**/*.test.ts?(x)</c>).</summary>
	public required string Glob { get; init; }

	/// <summary>Regex over a symbol's name; a match marks it a test. Its first capture group, if any, is the test name.</summary>
	public required string Symbol { get; init; }

	/// <summary>Command template to run a single test — supports <c>${file}</c>, <c>${fileDir}</c>, <c>${name}</c>.</summary>
	public required string RunOne { get; init; }

	/// <summary>Command template to run every test in a file — supports <c>${file}</c>, <c>${fileDir}</c>.</summary>
	public required string RunFile { get; init; }

	/// <summary>Separator joining captured names along the ancestor symbol chain (nested <c>describe</c>s). Defaults to a space.</summary>
	public string NameSeparator { get; init; } = " ";

	/// <summary>
	/// Optional regex matched against the source between a symbol's full range start and its name — the region
	/// that holds attributes/annotations/decorators (<c>[Fact]</c>, <c>@Test</c>). When set, a symbol is a test
	/// only if this also matches, letting attribute-based frameworks be selected as data.
	/// </summary>
	public string? Header { get; init; }
}

/// <summary>
/// A parsed <c>test.profile</c>: the ordered <see cref="Rules"/> a workspace configured. Parsing and validation
/// live here so the same logic backs both the settings validator and the run/lens consumers.
/// </summary>
public sealed record TestProfile {
	/// <summary>An empty profile (no rules) — the parse of an empty string or <c>[]</c>.</summary>
	public static TestProfile Empty { get; } = new() { Rules = [] };

	/// <summary>The rules, in the order the workspace declared them; the first whose glob matches a file wins.</summary>
	public required IReadOnlyList<TestRule> Rules { get; init; }

	/// <summary>The <see cref="SettingDefinition.Validate"/> adapter: a stored <c>test.profile</c> string must parse.</summary>
	public static ValidationResult Validate(object? value) =>
		value is not string text || TryParse(text, out _, out string? error)
			? ValidationResult.Success
			: ValidationResult.Failure(error);

	/// <summary>
	/// Parses <paramref name="json"/> (an empty string or a JSON array of rule objects) into a profile. An empty
	/// or whitespace string and <c>[]</c> both yield <see cref="Empty"/>. Fails with a human-readable
	/// <paramref name="error"/> on malformed JSON, a missing required field, or a non-compiling regex.
	/// </summary>
	public static bool TryParse(string json, out TestProfile profile, out string error) {
		profile = Empty;
		error = string.Empty;
		if (string.IsNullOrWhiteSpace(json)) {
			return true;
		}

		JsonElement root;
		try {
			using var doc = JsonDocument.Parse(json);
			root = doc.RootElement.Clone();
		} catch (JsonException ex) {
			error = $"test.profile is not valid JSON: {ex.Message}";
			return false;
		}

		if (root.ValueKind != JsonValueKind.Array) {
			error = "test.profile must be a JSON array of rules.";
			return false;
		}

		var rules = new List<TestRule>();
		int index = 0;
		foreach (var element in root.EnumerateArray()) {
			if (!TryParseRule(element, index, out var rule, out error)) {
				return false;
			}

			rules.Add(rule);
			index++;
		}

		profile = new TestProfile { Rules = rules };
		return true;
	}

	private static bool TryParseRule(JsonElement element, int index, out TestRule rule, out string error) {
		rule = null!;
		if (element.ValueKind != JsonValueKind.Object) {
			error = $"test.profile[{index}] must be an object.";
			return false;
		}

		if (!TryRequiredString(element, "glob", index, out string glob, out error)
			|| !TryRequiredString(element, "symbol", index, out string symbol, out error)
			|| !TryRequiredString(element, "runOne", index, out string runOne, out error)
			|| !TryRequiredString(element, "runFile", index, out string runFile, out error)) {
			return false;
		}

		if (!TryCompileRegex(symbol, index, "symbol", out error)) {
			return false;
		}

		string? header = null;
		if (element.TryGetProperty("header", out var headerElement) && headerElement.ValueKind != JsonValueKind.Null) {
			if (headerElement.ValueKind != JsonValueKind.String) {
				error = $"test.profile[{index}].header must be a string.";
				return false;
			}

			header = headerElement.GetString();
			if (!TryCompileRegex(header!, index, "header", out error)) {
				return false;
			}
		}

		string separator = " ";
		if (element.TryGetProperty("nameSeparator", out var sepElement) && sepElement.ValueKind != JsonValueKind.Null) {
			if (sepElement.ValueKind != JsonValueKind.String) {
				error = $"test.profile[{index}].nameSeparator must be a string.";
				return false;
			}

			separator = sepElement.GetString()!;
		}

		rule = new TestRule {
			Glob = glob,
			Symbol = symbol,
			RunOne = runOne,
			RunFile = runFile,
			NameSeparator = separator,
			Header = header,
		};
		return true;
	}

	private static bool TryRequiredString(JsonElement element, string name, int index, out string value, out string error) {
		value = string.Empty;
		error = string.Empty;
		if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) {
			error = $"test.profile[{index}] is missing required string field '{name}'.";
			return false;
		}

		value = property.GetString()!;
		if (string.IsNullOrEmpty(value)) {
			error = $"test.profile[{index}].{name} must not be empty.";
			return false;
		}

		return true;
	}

	private static bool TryCompileRegex(string pattern, int index, string field, out string error) {
		error = string.Empty;
		try {
			_ = new Regex(pattern);
			return true;
		} catch (ArgumentException ex) {
			error = $"test.profile[{index}].{field} is not a valid regex: {ex.Message}";
			return false;
		}
	}
}
