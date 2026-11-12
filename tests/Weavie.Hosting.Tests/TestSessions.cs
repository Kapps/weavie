using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Corrections;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Weavie.Core.Workflows;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Tests;

internal static class TestSessions {
	/// <summary>An activated session rooted at <paramref name="dir"/> over isolated stores and unused collaborators.</summary>
	public static HostSession Create(
		TempDirectory dir, SettingsStore settings, CommandRegistry commands, IPtyLauncher launcher, IAgentProvider provider) {
		var files = new LocalFileSystem();
		var endpoint = new HostMessageRouter(new FakeHostBridge(), new InlineUiDispatcher(), _ => { })
			.OpenSession(new SessionAddress("slot-1", Guid.NewGuid().ToString("n")));
		var session = new HostSession(
			endpoint,
			provider,
			[ShellTerminalId.New()],
			new SessionPaths {
				WorkspaceRoot = dir.Path,
				SettingsWorkspaceRoot = dir.Path,
				ScratchDir = dir.Combine("scratch"),
				PastedImagesDir = dir.Combine("pasted"),
				AuthenticationTerminalLog = dir.Combine("agent-pane.json"),
				ShellScrollback = id => dir.Combine($"shell-{id}.json"),
			},
			new SessionServices {
				Settings = settings,
				Layout = new LayoutStore(files, LayoutPanes.CreateRegistry(), dir.Combine("layout.json")),
				CommandRegistry = commands,
				Keybindings = new KeybindingStore(commands, dir.Combine("keybindings.json"), enableWatcher: false),
				ThemeOverrides = new ThemeOverridesStore(files, dir.Combine("theme-overrides.json")),
				Corrections = new CorrectionCorpus(files, dir.Combine("corrections.jsonl")),
				Inference = UnusedInferenceService.Instance,
				AgentConsultation = AgentConsultation.None,
				PtyLauncher = launcher,
				Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
				Workflow = new() {
					Roles = new WorkflowRoleStore(files, dir.Combine("workflow-roles.json")),
					DocumentsDirectory = id => dir.Combine("documents", id),
					Decider = _ => new UnusedWorkflowDecider(),
					RoleResolver = _ => NoWorkflowRoles.Instance,
				},
			},
			new SessionOwner {
				Peers = NoSessionPeers.Instance,
				InputFrozen = () => false,
				AcceptTerminalInput = (_, accept) => accept(),
				ShellResized = (_, _) => { },
			});
		session.ActivateOwnedRuntimeAndMessages();
		return session;
	}
}
