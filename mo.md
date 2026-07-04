# simpleagentchat Implementation Modus Operandi

This file describes how agents should implement and test `simpleagentchat`.
It complements `specs.md`; the spec remains the source of truth for behavior.

## Core Rule

Every meaningful implementation slice should be tested the way the tool is
meant to be used: from a fresh repository, through the public command line, and
then through the browser-facing UI when server or human workflows are involved.

Prefer evidence from real commands and real files over assumptions.

## Acceptance Harness

For each completed slice, create a temporary empty directory and turn it into a
Git repository:

```powershell
$tmp = Join-Path $env:TEMP ("simpleagentchat-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null
Set-Location $tmp
git init
```

Copy or reference the current `simpleagentchat.cs`, then exercise it through
the public command surface:

```powershell
dotnet simpleagentchat.cs init
dotnet simpleagentchat.cs say implementer "hello"
dotnet simpleagentchat.cs fetch --wait-ms 0 --json
dotnet simpleagentchat.cs goal status release.md
```

After each scenario, inspect the final repository state directly:

- `.simpleagentchat/` exists only where expected.
- `.gitignore` contains exactly one satisfying `.simpleagentchat/` ignore entry.
- `HOW_TO_CHAT.md` and `AGENTS.md` preserve user content outside marked blocks.
- role, goal, asset, status, message, `chat.html`, and `ui.html` files match
  the spec.
- invalid names, cursors, and paths fail without writing outside allowed
  locations.

Also verify the negative case: running `init` or `serve` outside a Git
repository must fail without creating files.

## Agent Simulation

When testing agent-facing behavior, use the tool like an agent would:

1. Run initial `fetch --json` with no cursor.
2. Preserve the returned `nextCursor`.
3. Read current goals.
4. Read the assigned role's `instructions.md`.
5. Read the assigned role's `role_memory.md`.
6. Check whether a human `Start` message already exists.
7. Before each meaningful step, fetch from the latest fetched cursor.
8. Do not advance the fetch cursor from the cursor returned by the agent's own
   `say`; advance only from `fetch` output.
9. Use `goal status` before claiming completion.
10. Use `goal done`, `goal undone`, and `goal recheck` only according to the
    goal-completion rules in `specs.md`.

Subagents may be used as instruction-clarity smoke tests. For example, ask one
to join as `implementer` and another as `reviewer`, then observe whether they
follow `HOW_TO_CHAT.md` without extra steering. If they repeatedly miss a rule,
update `HOW_TO_CHAT.md` or the generated role instructions so the next agent can
follow the workflow naturally.

Do not rely on subagent behavior as the primary correctness test. Deterministic
unit and integration tests are the source of truth.

## Browser and Server Checks

When a slice touches `serve`, the HTTP API, `ui.html`, assets, role editing,
goal editing, role memory editing, or human messages:

1. Start the server from a temporary Git repository.

   ```powershell
   dotnet simpleagentchat.cs serve --no-open --port 8765
   ```

2. Verify the server binds only to loopback.
3. Exercise the HTTP endpoints with real requests.
4. Use browser automation to inspect the human-facing UI.
5. Confirm UI actions write through the same internal paths as CLI actions.
6. Confirm role, role memory, and goal edits produce visible `system` messages.
7. Confirm critical human messages are normalized with `! ` when requested.
8. Confirm active uploaded asset types are served inertly or as attachments.

Browser checks should validate both behavior and usability: messages appear in
the transcript, editors load saved content, uploaded passive assets can be
viewed, and controls remain usable at normal desktop and narrow viewport sizes.

## Unit and Integration Testability

Although distribution is a single `simpleagentchat.cs` file, implementation
should still be internally testable. Keep the file organized around small
services and pure helpers, such as:

- command parsing
- safe role, goal, asset, and cursor validation
- repository-root discovery
- path containment checks
- initialization and marked-block updates
- message id creation and message-store operations
- fetch filtering and cursor watermark rules
- goal-status calculation and mutation
- Markdown escaping/rendering
- HTTP endpoint handlers

Gate only the executable entrypoint behind a test symbol if needed, for example:

```csharp
#if !SIMPLEAGENTCHAT_TEST
return await SimpleAgentChat.Program.MainAsync(args);
#endif
```

Then a test project can compile the same single-file implementation with
`SIMPLEAGENTCHAT_TEST` defined and call the internal services directly.

Recommended coverage:

- Unit tests for parsing, validation, path safety, status rules, and Markdown
  escaping.
- Integration tests that run real `dotnet simpleagentchat.cs ...` commands in
  temporary Git repositories and assert exit codes, stdout, stderr, and files.
- Server tests that start `serve --no-open` and verify HTTP endpoint contracts.
- Browser tests for the human UI workflows.
- Concurrency tests for simultaneous `say`, `fetch`, and goal-status updates.

## Slice Completion Standard

A slice is not complete just because code compiles. Before marking it done:

1. Run the most relevant unit tests.
2. Run at least one temp-repo command-line scenario covering the changed
   behavior.
3. If server/UI behavior changed, run server and browser checks.
4. Inspect the final `.simpleagentchat/` state and generated files.
5. Record the exact commands run and the outcome in the implementation handoff
   or final agent message.

Keep implementation slices small enough that failures point to one area, but
large enough to deliver a meaningful, spec-backed behavior.
