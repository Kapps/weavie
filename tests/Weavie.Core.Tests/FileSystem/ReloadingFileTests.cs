using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.FileSystem;

public sealed class ReloadingFileTests {
	[Fact]
	public async Task Watch_RetainsLastGoodValueUntilFileIsValid() {
		using var directory = new TempDirectory("weavie-reloading-file-tests");
		string path = directory.WriteFile("value.txt", "1");
		using var file = new ReloadingFile<int>(path, new Lock(), 0, Load, watch: true);
		var invalid = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var repaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		file.Reloaded += reload => {
			if (reload.Error is not null) {
				invalid.TrySetResult();
			} else if (reload.Value == 2) {
				repaired.TrySetResult();
			}
		};

		File.WriteAllText(path, "invalid");
		await invalid.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(1, file.Value);
		Assert.IsType<InvalidDataException>(file.Error);

		File.WriteAllText(path, "2");
		await repaired.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(2, file.Value);
		Assert.Null(file.Error);
	}

	private static int Load(string path) => int.TryParse(File.ReadAllText(path), out int value)
		? value
		: throw new InvalidDataException("Expected an integer.");
}
