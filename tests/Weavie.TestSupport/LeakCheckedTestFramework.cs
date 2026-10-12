using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Weavie.TestSupport;

/// <summary>
/// The xunit framework every test assembly runs under: it opens <see cref="TestRunRoot"/> before the first test
/// and closes it after the last, so a run that leaks temp state fails as an assembly cleanup failure.
/// </summary>
public sealed class LeakCheckedTestFramework(IMessageSink messageSink) : XunitTestFramework(messageSink) {
	/// <inheritdoc/>
	protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
		new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

	private sealed class Executor(AssemblyName assembly, ISourceInformationProvider sourceInformation, IMessageSink diagnostics)
		: XunitTestFrameworkExecutor(assembly, sourceInformation, diagnostics) {
		protected override async void RunTestCases(
			IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions) {
			using var runner = new Runner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
			await runner.RunAsync();
		}
	}

	private sealed class Runner(
		ITestAssembly assembly,
		IEnumerable<IXunitTestCase> testCases,
		IMessageSink diagnostics,
		IMessageSink execution,
		ITestFrameworkExecutionOptions options) : XunitTestAssemblyRunner(assembly, testCases, diagnostics, execution, options) {
		protected override async Task AfterTestAssemblyStartingAsync() {
			await base.AfterTestAssemblyStartingAsync();
			TestRunRoot.Open();
		}

		protected override async Task BeforeTestAssemblyFinishedAsync() {
			Aggregator.Run(TestRunRoot.Close);
			await base.BeforeTestAssemblyFinishedAsync();
		}
	}
}
