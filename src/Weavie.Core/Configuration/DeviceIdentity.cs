using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Configuration;

/// <summary>The stable identity of one Weavie root, named by the pages it serves. See <c>docs/specs/settings-ownership.md</c>.</summary>
public sealed record DeviceIdentity(
	[property: JsonPropertyName("id")] string Id,
	[property: JsonPropertyName("name")] string Name) {
	/// <summary>Reads the identity kept at <paramref name="path"/>, creating one named after this machine when there is none.</summary>
	public static DeviceIdentity LoadOrCreate(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		if (File.Exists(path)) {
			return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.DeviceIdentity)
				?? throw new JsonException($"{path} holds no device identity.");
		}

		// Written whole and moved into place without replacing, so two hosts starting together agree on one identity.
		var identity = new DeviceIdentity(Guid.NewGuid().ToString("n"), Environment.MachineName);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string staged = $"{path}.{identity.Id}";
		File.WriteAllText(staged, JsonSerializer.Serialize(identity, CoreJson.Default.DeviceIdentity));
		try {
			File.Move(staged, path, overwrite: false);
			return identity;
		} catch (IOException) when (File.Exists(path)) {
			File.Delete(staged);
			return LoadOrCreate(path);
		}
	}
}
