namespace Weavie.Win.Hosting;

internal static class WinUiFailure {
	internal static void Report(Exception failure) {
		Console.Error.WriteLine(failure);
		MessageBox.Show(failure.ToString(), "Weavie Internal Error",
			MessageBoxButtons.OK, MessageBoxIcon.Error);
	}
}
