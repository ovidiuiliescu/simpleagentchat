# simpleagentchat

`simpleagentchat` is quick and dirty inter-agent communication for local coding work:

- Helps different agents, like Codex and Claude Code, collaborate seamlessly in the same repo.
- One C# file that provides shared public chat, role instructions, goal status, and small asset handoffs.
- Works entirely inside a Git repository, with no hosted service, account, database, or project-specific integration.
- Not an orchestrator: no scheduling, routing, or workflow automation.

![simpleagentchat browser UI showing Codex and Claude roles collaborating on a README demo goal](docs/simpleagentchat-in-action.png)

## What It Does

- Stores chat state in local files under `.simpleagentchat/`.
- Gives each agent an explicit role with instructions and durable role memory.
- Lets agents and humans exchange Markdown messages and manage roles, goals, and assets through a CLI or local browser UI.
- Tracks shared goal agreement with `done`, `undone`, `status`, and `recheck` commands.
- Imports and exports selected room content as a zip file when you want to move or snapshot goals, roles, messages, goal status, or assets.
- Keeps the local server optional; agents can coordinate with plain command-line polling.

## Requirements

- A Git repository.
- The .NET 10 SDK or newer, with support for file-based C# apps.

## Quick Start

- Copy `simpleagentchat.cs` into your repo.
- Run:

```powershell
dotnet .\simpleagentchat.cs serve
```

- Edit your agent roles and goals in the browser-based UI that pops up, or manage roles, goals, assets, and import/export archives with the CLI.
- Use the role panel's `Copy prompt` button, then paste that prompt into Claude, Codex, or your favorite harness.
- Once everything is configured, say `Start` in the chat window.
- Watch the magic happen.

Agents use the shared prebuilt runner, so adding roles does not trigger another build:

```powershell
dotnet .\.simpleagentchat\runner\simpleagentchat-runner.dll join reviewer --json
```

`join` returns the role instructions, durable memory, goals, current approvals, prior public chat, whether the human has said `Start`, and the next fetch cursor in one response.

## Development

The distributable remains the single sectioned `simpleagentchat.cs` file. Run the repository verification command with:

```powershell
.\verify.ps1
```

The test project is intentionally a dependency-free console harness, so use `verify.ps1` or `dotnet run --project tests\SimpleAgentChat.UnitTests\SimpleAgentChat.UnitTests.csproj`; `dotnet test` does not execute this harness.
