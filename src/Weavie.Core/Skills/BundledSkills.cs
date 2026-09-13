using System.Text;
using System.Text.Json;

namespace Weavie.Core.Skills;

/// <summary>Discovers only the skills shipped alongside the running Weavie host.</summary>
public static class BundledSkills {
	/// <summary>Builds a discovery catalog without loading skill bodies into the agent's context.</summary>
	public static string Catalog() => Catalog(Path.Combine(AppContext.BaseDirectory, "skills"));

	internal static string Catalog(string root) {
		string[] paths = Directory.GetFiles(root, "SKILL.md", SearchOption.AllDirectories);
		if (paths.Length == 0) throw new InvalidDataException($"No bundled Weavie skills found in {root}.");
		var catalog = new StringBuilder("""
			## Weavie-only skills
			These skills are bundled with Weavie and available in this session. When the user names a skill
			or their request matches its description, read its SKILL.md before proceeding. Resolve relative
			references against the skill's directory and read the references it requires. Use your file-reading
			tools; if a skill cannot be read, report the failure rather than proceeding without its instructions.
			""");
		foreach (string path in paths.Order(StringComparer.Ordinal)) {
			using var reader = File.OpenText(path);
			if (reader.ReadLine() != "---") throw new InvalidDataException($"Missing skill frontmatter: {path}");
			var frontmatter = new StringBuilder();
			string? line;
			while ((line = reader.ReadLine()) is not null && line != "---") frontmatter.AppendLine(line);
			if (line is null) throw new InvalidDataException($"Unterminated skill frontmatter: {path}");
			// Bundled frontmatter uses JSON, a YAML subset, so the standard skill metadata needs no extra parser.
			using var metadata = JsonDocument.Parse(frontmatter.ToString());
			string? name = metadata.RootElement.GetProperty("name").GetString();
			string? description = metadata.RootElement.GetProperty("description").GetString();
			if (name != Path.GetFileName(Path.GetDirectoryName(path)) || string.IsNullOrWhiteSpace(description)) {
				throw new InvalidDataException($"Invalid bundled skill metadata: {path}");
			}
			catalog.Append($"\n- {name}: {description} (file: {Path.GetFullPath(path)})");
		}
		return catalog.ToString();
	}
}
