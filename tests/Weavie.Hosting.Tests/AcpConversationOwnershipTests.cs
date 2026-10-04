using System.Reflection;
using System.Runtime.CompilerServices;
using Weavie.AgentClientProtocol;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

// A conversation is an owned incarnation: it reaches the process only through its endpoint and its owner only
// through its port, so nothing in it can address another incarnation's process, session, or storage.
public sealed class AcpConversationOwnershipTests {
	private const BindingFlags Declared =
		BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

	private static readonly string[] OwnershipNames = ["generation", "epoch"];

	[Fact]
	public void ConversationHoldsNoOwnerConnectionOrStore() {
		Type[] forbidden = [typeof(AcpJsonRpcConnection), typeof(AcpAgentSession), typeof(AcpSessionStore), typeof(AcpControlStore)];
		Assert.Empty(Fields().Where(field => forbidden.Contains(field.FieldType)).Select(Describe));
	}

	[Fact]
	public void EndpointAndPortAreAssignedOnce() {
		var endpoint = Assert.Single(Fields(), field => field.FieldType == typeof(AcpOnce<AcpSessionEndpoint>));
		var port = Assert.Single(Fields(), field => field.FieldType == typeof(AcpConversationPort));

		Assert.True(endpoint.IsInitOnly);
		Assert.True(port.IsInitOnly);
		Assert.DoesNotContain(Fields(), field => field.FieldType == typeof(AcpSessionEndpoint));
	}

	[Fact]
	public void PortIsTheOnlyEventSink() {
		Assert.All(
			Fields().Where(field => typeof(IAgentEventSink).IsAssignableFrom(field.FieldType)),
			field => Assert.Equal(typeof(AcpConversationPort), field.FieldType));
	}

	[Fact]
	public void NoMemberTakesAProcessAddressOrSessionIdentity() {
		Type[] forbidden = [typeof(AcpJsonRpcConnection), typeof(AcpSessionEndpoint)];
		var parameters = Types()
			.SelectMany(type => type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
			.Where(method => !method.IsDefined(typeof(CompilerGeneratedAttribute)))
			.SelectMany(method => method.GetParameters().Select(parameter => (Method: method, Parameter: parameter)));

		Assert.Empty(parameters
			.Where(entry => forbidden.Contains(entry.Parameter.ParameterType)
				|| Names(entry.Parameter.Name!, [.. OwnershipNames, "sessionId"]))
			.Select(entry => $"{entry.Method.DeclaringType!.Name}.{entry.Method.Name}({entry.Parameter.Name})"));
		// The conversation's own continuation keeps its provider session; nothing else is addressed by one.
		Assert.Empty(Fields().Where(field => Names(field.Name, OwnershipNames)).Select(Describe));
	}

	private static IEnumerable<Type> Types() => typeof(AcpConversation)
		.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
		.Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute)))
		.Prepend(typeof(AcpConversation));

	private static IEnumerable<FieldInfo> Fields() => Types()
		.SelectMany(type => type.GetFields(Declared))
		.Where(field => !field.IsDefined(typeof(CompilerGeneratedAttribute)));

	private static bool Names(string name, IEnumerable<string> fragments) =>
		fragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

	private static string Describe(FieldInfo field) => $"{field.DeclaringType!.Name}.{field.Name}";
}
