using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Corrections;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Suggestions;
using Weavie.Core.Theming;
using Weavie.Core.Workflows;
using Weavie.Core.Worktrees;
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
				Repo = PlainRepo(dir, settings, files),
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
				Inference = UnusedInferenceService.Instance,
				AgentConsultation = AgentConsultation.None,
				PtyLauncher = launcher,
				Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
				SettingsTools = new SettingsTools(settings, "test-machine"),
				Workflow = new() {
					Roles = new WorkflowRoleStore(files, dir.Combine("workflow-roles.json")),
					DocumentsDirectory = id => dir.Combine("documents", id),
					Decider = _ => new UnusedWorkflowDecider(),
					RoleResolver = _ => NoWorkflowRoles.Instance,
				},
			},
			new SessionOwner {
				Peers = NoSessionPeers.Instance,
				Device = TestDevice.Home(settings),
				InputFrozen = () => false,
				AcceptTerminalInput = (_, accept) => accept(),
				ShellResized = (_, _) => { },
			});
		session.ActivateOwnedRuntimeAndMessages();
		return session;
	}

	/// <summary>A repo over the plain folder <paramref name="dir"/>, with isolated stores and suggestions that never run.</summary>
	private static Repo PlainRepo(TempDirectory dir, SettingsStore settings, LocalFileSystem files) {
		var corrections = new CorrectionCorpus(files, dir.Combine("corrections.jsonl"));
		return new Repo {
			Root = dir.Path,
			Worktrees = null,
			Provisioner = NullWorktreeProvisioner.Instance,
			Corrections = corrections,
			RecentFiles = new RecentFilesStore(files, dir.Combine("recent-files.json")),
			Suggestions = new SuggestionService(
				CoreSuggestions.CreateRegistry(), settings, files, dir.Path,
				new SuggestionDismissals(files, dir.Combine("suggestions.json")), TimeSpan.Zero, _ => { }, () => false,
				() => new SuggestionSignals(corrections.Count, new DecisionHistorySummary(0, TimeSpan.Zero))),
		};
	}
}
