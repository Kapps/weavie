# weavie

Weavie is an agentic code editor that weaves Claude Code, terminal sessions, and full code editing into one workflow.

It's designed for a terminal-first development flow where Claude Code handles the bulk of the changes, but you as a
developer review each of the changes and make edits in a fully featured editor without breaking your flow.

<img width="3403" height="1869" alt="Screenshot" src="https://github.com/user-attachments/assets/b37d3198-1fa4-4bc7-985a-1e27104b39a8" />

## Features
- **Fully featured editor**: The editor is a first class development environment with full LSP support. This includes autocomplete, go to definition, refactoring, running tests, and more.
- **Parallel development**: Weavie has first class support for sessions, so you can work on several features at a time in their own branches.
- **Seamless remote/local sessions**: Each session can run either locally or remote, and the experience is identical between them. This includes hooks into Claude Code to make features like remote copy/paste, pasting images, URL opening, etc, all work just like they would locally.
- **Keyboard first**: Most actions in weavie are keyboard first by design. UI elements are designed to get out of your way.
- **Context aware**: Weavie is an MCP server, and integrates with Claude directly. Claude know what file you're in, and what lines you selected. It can even edit weavie settings and themes — for example, you can ask Claude to make all semantic highlighting 20% darker.
- **Uses the Claude Code TUI**: Weavie embeds the Claude Code TUI directly. This ensures that you can continue using your subscription plan rather than API pricing if/when [that change](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan) goes through.
- **Custom sources**: You can open a Github PR directly, opening the branch as a worktree and using the existing weavie diff view to review it. You can also open an existing Notion doc with very basic editing support.

## State of things

At this point, weavie is my daily driver and has been what I've used to develop the product.
There are limitations though:
- Currently only C#, JS/TS, and Go are supported. Rust and Python are planned to be added.
- No plugin support — you can install vsix themes, but that's all.
- Linux client support is untested. Headless support as a remote runner is supported, but I haven't tested running the weavie client itself on a Linux machine.
- Most Git features only support Github currently.
- There's still a lot of polish to be added.
- No releases or installer — build it from source.
- Nothing yet around packaging, signing, or shipping an actual binary.

## Building it

Needs a .NET 10 SDK (global.json rolls forward and allows prerelease), Node for the UI, and
Claude Code (`claude`) on the PATH since weavie spawns it. The UI uses pnpm, run through Corepack
(bundled with Node) — the `corepack pnpm` commands below need no separate install; `corepack enable`
once puts a bare `pnpm` on the PATH if you prefer it. On Windows that also means Windows 10 1809+ and
the WebView2 runtime (already there on Windows 11). For LSP, put `csharp-ls`, `gopls`, or `tsgo` on
the PATH.

## License

MIT — see [LICENSE](LICENSE).
