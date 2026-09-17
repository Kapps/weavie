# weavie

Weavie is an agentic code editor that weaves Claude Code, Codex, or other ACP agents, terminal sessions, and full code editing into one workflow.

It's designed for a workflow where you develop agent-first, but still care about the code and want easy access to review and make changes.

The goal is to support the workflow that a software engineer is likely to use in their day-to-day: manual code changes, reviewing PRs, or even editing Notion docs directly in the IDE.

<img width="3403" height="1869" alt="Screenshot" src="https://github.com/user-attachments/assets/b37d3198-1fa4-4bc7-985a-1e27104b39a8" />

## Features
- **Fully featured editor**: The editor is a first class development environment with full LSP support. This includes autocomplete, go to definition, refactoring, running tests, and more.
- **Parallel development**: Weavie has first class support for worktrees, so you can work on several features at a time in their own branches.
- **Seamless remote/local sessions**: Each worktree can run either locally or remote, and the experience is identical between them. This includes hooks into Claude Code to make features like remote copy/paste, pasting images, URL opening, etc, all work just like they would locally.
- **Context aware**: Weavie gives your agent context from the editor, including what file is open and what lines are selected. No more copy/pasting content back and forth. It can even edit weavie settings and themes — for example, you can ask it to make all semantic highlighting 20% darker.
- **Agent agnostic**: You can either use an embedded Claude Code TUI (which still receives weavie context), or use weavie's agent UI with any harness via ACP.
- **Fully cross-platform**: Weavie runs on Windows, Linux, MacOS, and is also designed to smoothly run on mobile as a PWA (primarily tested on iOS).

## Current State

At this point, weavie is my daily driver for both professional and personal projects.
There are limitations though:
- C#, JS/TS, and Go have built-in editor, LSP, workspace-setup, and test support. Python and Rust are supported but untested.
- No plugin support — you can install vsix themes from a file or the registry, as well as add an ACP provider from the registry, but no general plugins are supported yet.
- Most Git features only support Github currently.
- There's still more polish to be added.

If you run into bugs, performance issues, or missing features, please [file an issue](https://github.com/Kapps/weavie/issues/new)!

## Getting Started

### Client Setup

1. Download the [latest stable release](https://github.com/Kapps/weavie/releases/latest) for your OS.
2. On Linux, install GTK 3 and WebKitGTK 4.1 version 2.42 or newer.
3. Run it and open your repo. If running tests or creating a worktree doesn't work out of the box, ask your agent to configure it for your repo.

### Make It Yours

- **Run a command**: Open the command palette with `Ctrl+Shift+P` by default.
- **Set a theme**: Run **Select Color Theme…** from the palette. Toggle dark mode with `Ctrl+Shift+M`.
- **Ask your agent**: Try "What can I do in weavie?", "How do I run tests?", or "Make the editor font bigger." It can look up commands and settings, explain them, and change them for you.

### Use Codex or Another ACP Agent

ACP (Agent Client Protocol) lets different agents work in weavie's native chat UI.

1. Run **Manage ACP Agents…** from the command palette.
2. Find Codex or another agent in the registry and install it. Some agents need Node.js or uv installed on the machine running the session.
3. Create a new session and choose that agent in the agent picker.

Many ACP providers, include Claude and Codex, use the corresponding CLI on your machine. This means you would authenticate with the CLI, and can use your subscription plan.

### Remote Runner Setup
**Remote setup must use a VPN or Tailscale. DO NOT expose the headless server to the internet.** There's authentication, but aside from the authentication piece, the remote code hasn't been properly looked at.

1. Download the [latest stable release](https://github.com/Kapps/weavie/releases/latest) of the Runner
   - Prebuilt remote runner binaries are currently only provided for Linux.
2. If using Tailscale, make sure to go into your admin console and enable Tailscale Serve.
3. Run the runner with a stable generated token.
   - Tailscale example: `./current/Weavie.Runner --tls tailscale --token <random token> --workspace <workspace folder to serve>`
   - To enable automatic updates, include `--auto-update`. The client app should generally match the server version for stability.
4. For browser or mobile access, open the HTTPS control-plane URL printed by the runner and enter that token
   once. The browser remembers it and opens the worker at a clean URL.
   - For mobile, adding it as a Progressive Web App is strongly recommended.
5. In each native client you want to connect to this server, go into the Cloud section in the rail, and select
   Add Remote Agent.
   - Example URL: `https://<servername>.tail<random>.ts.net`
   - Token must exactly match the one you used to start the Runner.
6. Now when you launch a new session, you can select whether to run it locally or on this remote runner.

## Contributing / Reporting Issues

Please report any issues or feature requests by [creating an issue](https://github.com/Kapps/weavie/issues/new).

Candidly, while weavie is open source, **pull requests (for features especially) are unlikely to be accepted**. I have a plan for how I want things done and where I want weavie to go, and weavie development has me more in an architect / tester / project manager role with the agent handling the implementation. Development has been extremely fast, and code writing is not the bottleneck. External PRs require a lot more careful review (especially for security), and I still have to perform the same roles as if an agent wrote, it but with more manual testing. 

## License

MIT — see [LICENSE](LICENSE).
