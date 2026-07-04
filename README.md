# simpleagentchat

`simpleagentchat` is quick and dirty inter-agent communication for local coding work: one C# file that gives humans and agents a shared public chat, role instructions, goal status, and small asset handoffs inside a Git repository without a hosted service, account, database, or project-specific integration.

## What It Does

- Stores chat state in local files under `.simpleagentchat/`.
- Gives each agent an explicit role with instructions and durable role memory.
- Lets agents and humans exchange Markdown messages through a CLI or local browser UI.
- Tracks shared goal agreement with `done`, `undone`, `status`, and `recheck` commands.
- Keeps the local server optional; agents can coordinate with plain command-line polling.

## Requirements

- A Git repository.
- A modern .NET SDK that can run file-based C# apps with `dotnet simpleagentchat.cs`.

## Quick Start

Run the local UI from the repository where you want agents to coordinate:

```powershell
dotnet simpleagentchat.cs serve
```

`serve` initializes `.simpleagentchat/` if needed, starts a local server, and opens or prints the browser UI. Use the UI to create or edit roles, add goals, upload assets, and send human chat messages.

For setup without starting the UI:

```powershell
dotnet simpleagentchat.cs init
```

## CLI Basics

Send a public Markdown chat message as an existing role:

```powershell
dotnet simpleagentchat.cs say implementer "I am starting on the parser tests."
```

Fetch chat messages. Agents should keep the returned `nextCursor` and poll from it:

```powershell
dotnet simpleagentchat.cs fetch --json
dotnet simpleagentchat.cs fetch <cursor> --wait-ms 300000 --json
```

On Windows, .NET file-based apps can contend for their cached build output when multiple instances of the same `.cs` file run at the same time. If `serve` or a long-poll `fetch` is already running, use the no-build command form for parallel CLI work:

```powershell
dotnet build .\simpleagentchat.cs
dotnet run --file .\simpleagentchat.cs --no-build -- fetch --json
dotnet run --file .\simpleagentchat.cs --no-build -- fetch <cursor> --wait-ms 300000 --json
dotnet run --file .\simpleagentchat.cs --no-build -- say implementer "message"
```

If another `simpleagentchat` process is already running and the first command cannot rebuild, skip straight to the `dotnet run --file ... --no-build -- ...` form if the app was built earlier on that machine.

Check and update goal agreement:

```powershell
dotnet simpleagentchat.cs goal status release.md --json
dotnet simpleagentchat.cs goal done implementer release.md
dotnet simpleagentchat.cs goal undone implementer release.md
dotnet simpleagentchat.cs goal recheck release.md "Implementation changed; please review again."
```

Export a static transcript:

```powershell
dotnet simpleagentchat.cs export-html
```

## How Agents Should Join

After initialization, agents should read `HOW_TO_CHAT.md`, their role files under `.simpleagentchat/roles/<role>/`, and the goal files under `.simpleagentchat/goals/`. They should fetch the current chat context before acting, wait for a human `Start` when appropriate, and continue listening for new messages until the goal is done or they are explicitly told to stop.

All chat state is local and public to participants with repository access. Put supporting files in `.simpleagentchat/assets/` before referencing them from chat.

## License

MIT. See [LICENSE](LICENSE).
