# simpleagentchat Specification

## Purpose

`simpleagentchat` is a small, portable coordination tool for humans and coding agents such as Codex, Claude Code, OpenCode, and similar local agents.

The goal is to let multiple participants collaborate in a repository using only files on disk plus a single C# file. Agents exchange public chat messages, read shared goals and role instructions, and coordinate work without needing a hosted service, external account, database, or project-specific integration.

The intended distribution is one file:

```powershell
dotnet simpleagentchat.cs serve
```

The `serve` command is the human-friendly one-stop shop. It initializes the repository if needed, starts a tiny local server, and opens or prints a local UI for configuring roles, configuring goals, adding assets, and chatting with agents.

Agents can also use command-line operations directly:

```powershell
dotnet simpleagentchat.cs say <role> <markdown message>
dotnet simpleagentchat.cs fetch [cursor] [wait-ms]
dotnet simpleagentchat.cs fetch [--wait-ms <ms>]
dotnet simpleagentchat.cs goal status <goal_file_name>
dotnet simpleagentchat.cs goal done <role> <goal_file_name>
dotnet simpleagentchat.cs goal undone <role> <goal_file_name>
dotnet simpleagentchat.cs goal recheck <goal_file_name> <reason>
dotnet simpleagentchat.cs export-html
```

## Design Principles

- File-based first: durable chat state lives in files under `.simpleagentchat/`.
- Public by default: all chat messages are visible to all participants.
- Role-directed collaboration: agents participate under explicit role files.
- Human-controlled start: agents do not begin work until the human explicitly says `Start`.
- No hidden service dependency: the local server is optional for agents and exists to provide a nicer human UI.
- Portable and minimal: avoid heavy local dependencies, build systems, and generated clutter.
- Recoverable: generated views can be rebuilt from canonical message files.
- Conservative repo mutation: create missing files and append marked instruction blocks, but do not rewrite user-owned content casually.

## Requirements

The tool targets .NET file-based apps and should assume a modern .NET SDK that supports:

```powershell
dotnet simpleagentchat.cs <command>
```

The tool should not require a `.csproj` file in the host repository.

## Repository Layout

When initialized, the repository contains:

```text
.simpleagentchat/
  assets/
  goal_status/
  goals/
  messages/
  roles/
    implementer/
      instructions.md
      role_memory.md
    reviewer/
      instructions.md
      role_memory.md
  state.json
  ui.html
HOW_TO_CHAT.md
AGENTS.md
```

`.simpleagentchat/` is the active chat folder. There is intentionally no `.currentchat` file in v1. Agents and humans should assume the active chat lives at:

```text
<repo root>/.simpleagentchat/
```

The tool must add `.simpleagentchat/` to `.gitignore`.

## Repository Root and Path Boundaries

All tool-managed paths are resolved from the repository root. By default, every command starts at the current working directory and discovers the repository root by:

1. Running `git rev-parse --show-toplevel` when `git` is available.
2. Falling back to walking upward until a `.git` directory or `.git` file is found.
3. Failing with a non-zero exit code if no Git repository root is found.

The tool should not silently initialize an arbitrary non-Git directory. A future explicit `--root <path>` or `--allow-no-git` option may be added, but v1 should prefer safety over convenience.

The implementation may write only these root-relative locations:

- `.simpleagentchat/**`
- `.gitignore`
- `HOW_TO_CHAT.md`
- `AGENTS.md`

The server must never serve or mutate arbitrary repository files. Every user-supplied path or name must be converted to a full path and validated to remain under the intended base directory before any read, write, delete, or serve operation happens.

Allowed base directories:

- roles: `.simpleagentchat/roles/`
- goals: `.simpleagentchat/goals/`
- goal status: `.simpleagentchat/goal_status/`
- assets: `.simpleagentchat/assets/`
- messages: `.simpleagentchat/messages/`

### Safe Names

Role names are directory names and must match:

```text
[a-z][a-z0-9_-]{0,63}
```

Reserved role names:

- `goal`
- `human`
- `system`

`goal` is reserved for tool-generated public goal-coordination chat messages.

Goal and asset file names must match:

```text
[A-Za-z0-9][A-Za-z0-9._-]{0,127}
```

Names must not contain path separators, drive prefixes, URL encodings of path separators, control characters, leading dots, `..`, trailing spaces, or trailing dots. v1 should not support nested goal or asset directories.

For Windows portability, role names, goal file names, and asset file names must reject Windows device names case-insensitively, with or without extensions:

```text
CON, PRN, AUX, NUL, COM1-COM9, LPT1-LPT9
```

Examples to reject:

```text
con
NUL.txt
com1.md
goal.
```

Role deletion may delete only the resolved `.simpleagentchat/roles/<role>/` directory for a valid role name. Goal deletion may delete only the resolved `.simpleagentchat/goals/<name>` file for a valid goal name. Asset deletion, if implemented, may delete only the resolved `.simpleagentchat/assets/<name>` file for a valid asset name.

## Idempotent Initialization and File Updates

`init` and the initialization phase of `serve` must be idempotent.

Rules:

- Create `.simpleagentchat/`, `assets/`, `goals/`, `messages/`, and `roles/` if missing.
- Create `.simpleagentchat/goal_status/` if missing.
- If no valid role directories exist, create the default `implementer` and `reviewer` role directories.
- For every valid role directory, create `instructions.md` if missing and create `role_memory.md` if missing.
- Never overwrite existing role instructions, role memory, goals, goal status files, assets, or message files during initialization.
- Create `.gitignore` if missing.
- Add `.simpleagentchat/` to `.gitignore` once. Treat existing `.simpleagentchat` and `.simpleagentchat/` entries as already satisfying this requirement.
- Preserve all existing `.gitignore` content and line endings as much as practical.
- Create or update the simpleagentchat block in `HOW_TO_CHAT.md` using the markers below.
- Create or update the simpleagentchat block in `AGENTS.md` using the markers below.
- Preserve all content outside the marked blocks.

Markdown instruction block markers:

```text
<!-- simpleagentchat:start -->
...
<!-- simpleagentchat:end -->
```

If a file exists without the markers, append the marked block. If the markers already exist, replace only the content between them.

### `.simpleagentchat/messages/`

This folder is the canonical chat log. Each message is stored as an immutable message file.

Message filenames and ids use this canonical shape:

```text
yyyyMMddTHHmmss.fffffffZ-role-random
```

Example:

```text
20260704T123456.1234567Z-implementer-a1b2c3
```

The file for that message is:

```text
20260704T123456.1234567Z-implementer-a1b2c3.json
```

Message ids must match:

```text
\d{8}T\d{6}\.\d{7}Z-[a-z][a-z0-9_-]{0,63}-[a-f0-9]{6,16}
```

Exported or served HTML transcript views are derived from these message files. If `chat.html` is needed, it can be generated explicitly from `messages/`.

### `.simpleagentchat/roles/`

Each role is a directory containing instructions and long-term role memory.

Default roles:

- `implementer/instructions.md`: implements the goal cleanly and efficiently using pragmatic DRY and YAGNI principles.
- `reviewer/instructions.md`: performs a thorough code review, looks for gaps the implementer may have missed, flags serious concerns clearly, and asks questions in chat when implementation choices are unclear.

Each role directory also contains:

- `role_memory.md`: persistent role-specific memory, also called the role's thoughts file, for useful long-term thoughts, decisions, learnings, handoff notes, and context that should survive across agent sessions.

The UI should let the human create, edit, and delete roles.

If a role's instructions change, agents using that role must re-read the changed files before continuing. If a role directory is deleted, agents using that role must stop working.

When an agent joins as a role, it should read both files:

```text
.simpleagentchat/roles/<role>/instructions.md
.simpleagentchat/roles/<role>/role_memory.md
```

Agents may update their own `role_memory.md` directly on the filesystem with concise, durable notes that help the same role resume well in a later session. Agent-authored role memory updates do not need to generate `system` messages. Role memory is not private; all chat state is public and local.

### `.simpleagentchat/goals/`

This folder contains one or more goal files for the chat. Goal files can be any practical local format, such as `.txt`, `.md`, `.html`, or similar.

The UI should let the human create, edit, and delete goals.

When goals change, agents must re-read the goals folder before continuing.

### `.simpleagentchat/goal_status/`

This folder contains current per-role completion state for each goal file. Completion state is tool-managed metadata, but every status change must also be visible in the public chat log.

Recommended status filename shape:

```text
<goal_file_name>.status.json
```

Example for `.simpleagentchat/goals/release.md`:

```text
.simpleagentchat/goal_status/release.md.status.json
```

Status files should use this shape:

```json
{
  "goal": "release.md",
  "complete": false,
  "roles": {
    "implementer": {
      "status": "done",
      "updatedAtUtc": "2026-07-04T12:34:56.1234567Z",
      "messageId": "20260704T123456.1234567Z-implementer-a1b2c3"
    },
    "reviewer": {
      "status": "undone",
      "updatedAtUtc": "2026-07-04T12:40:00.0000000Z",
      "messageId": "20260704T124000.0000000Z-reviewer-d4e5f6"
    }
  }
}
```

Rules:

- A goal is complete only when every current valid role directory has explicitly marked that goal `done`.
- Roles with no status entry for a goal are treated as `undone`.
- Deleted roles are ignored for completion. Newly created roles start as `undone` for every existing goal.
- A role may withdraw agreement at any time by marking the goal `undone`.
- Editing a goal file through the UI resets completion state for that goal to `undone` for all current roles.
- Deleting a goal file should delete its status file if present.

### `.simpleagentchat/assets/`

This folder contains assets that humans or agents want to reference from chat messages. Examples include screenshots, logs, generated reports, documents, or other supporting files.

Participants should add files to `assets/` first, then mention or link to them in a chat message.

### `.simpleagentchat/state.json`

This file may store tool-managed state, such as UI preferences, last generated message cursor, schema version, or other non-authoritative metadata.

The canonical chat history remains the message files.

### `.simpleagentchat/chat.html`

This is an exported chat transcript generated only when the human or an agent explicitly runs the HTML export command. It renders Markdown messages as HTML with timestamps, roles, message ids, and styling.

This file should be safe to open directly from disk for read-only viewing. It may use a lightweight timed refresh when opened as a `file://` document, but browser security means it cannot write chat messages or call the CLI by itself.

### `.simpleagentchat/ui.html`

This is the local-server UI shell used by `serve`. It must include:

- live chat transcript rendered from the local server message API
- human message prompt
- role editor
- role memory/thoughts editor
- goal editor
- asset browser or upload form
- controls for sending critical messages

The UI should call the local server started by:

```powershell
dotnet simpleagentchat.cs serve
```

The browser UI should write through the same internal message path as the CLI `say` command.

## Root Instruction Files

### `HOW_TO_CHAT.md`

`serve` or `init` creates this file if missing.

It explains:

- the active chat lives in `.simpleagentchat/`
- agents must fetch all prior messages before joining
- an initial fetch with no cursor should omit historical `system` messages by default because current role and goal files are authoritative
- agents must read their role instructions and role memory before participating
- agents must read the goals folder before starting
- agents must wait for an explicit human `Start` before new work begins unless a prior `Start` already appears in the fetched chat history
- agents re-joining an existing role must continue from where that role left off instead of acting like a new participant
- a goal is complete only when all current roles have publicly marked it done
- agents should use `goal status <goal_file_name>` before claiming a goal is complete
- agents should use `goal undone <role> <goal_file_name>` if later changes invalidate their previous agreement
- agents should use `goal recheck <goal_file_name> <reason>` when important changes require all roles to re-approve a goal
- agents must track the `nextCursor` returned by `fetch`, not the cursor returned by their own `say`
- agents must fetch from their latest fetched cursor before each meaningful work step
- agents must obey critical `!` messages immediately
- agents must obey newly fetched `system` messages immediately
- agents must re-read role instructions, role memory, and goals when instructed
- agents must stop if their role no longer exists
- all chat is public
- assets should be placed in `.simpleagentchat/assets/` before being referenced

### `AGENTS.md`

`serve` or `init` should create or append a small marked block to `AGENTS.md`.

The block should say, in substance:

```text
If you are asked to join a simpleagentchat chat, read HOW_TO_CHAT.md first and follow it.
```

If `AGENTS.md` already exists, the tool should append a clearly marked block instead of rewriting unrelated content.

## Commands

CLI parsing rules:

- Command names are case-sensitive lowercase.
- Options are order-insensitive after the command name unless a command section defines narrower parsing rules.
- Unknown options fail with a non-zero exit code.
- Positional arguments keep their documented order.
- `wait-ms` values must be integers from `0` through `2147483647`.
- Successful commands write machine-relevant output to stdout.
- Errors write diagnostics to stderr.
- Exit code `0` means success, including a successful `fetch` timeout response.
- Exit code `1` means validation, usage, path-safety, or parsing failure.
- Exit code `2` means a lock or wait timeout prevented a mutation command such as `say` from completing.
- Exit code `3` means a local server startup failure such as an unavailable requested port.

### `serve`

```powershell
dotnet simpleagentchat.cs serve [--port <port>] [--no-open]
```

`serve` is the primary human entry point.

It should:

1. Find the repository root.
2. Initialize `.simpleagentchat/` if missing.
3. Ensure required folders exist.
4. Ensure default role directories and files exist if no roles are present.
5. Ensure `.simpleagentchat/` is ignored by git.
6. Ensure `HOW_TO_CHAT.md` exists.
7. Ensure `AGENTS.md` contains the simpleagentchat instruction block.
8. Write or refresh `ui.html`.
9. Start a tiny localhost server.
10. Open the UI in a browser or print the URL.

The server should expose only local loopback addresses.

The server should let the human:

- send a `human` chat message
- create, edit, and delete role instructions
- view and edit role memory/thoughts files
- create, edit, and delete goal files
- view assets
- upload assets using safe asset filenames
- view the rendered chat transcript

Any role, role memory, or goal change made through the UI must generate a `system` chat message.

### `init`

```powershell
dotnet simpleagentchat.cs init
```

`init` performs the initialization portion of `serve` without starting the local server.

This command is useful for scripts and agents. It should be idempotent.

### `say`

```powershell
dotnet simpleagentchat.cs say <role> [--wait-ms <ms>] <markdown content till end of line>
dotnet simpleagentchat.cs say <role> [--wait-ms <ms>] --stdin
dotnet simpleagentchat.cs say <role> [--wait-ms <ms>] --file <path>
```

Adds a new public chat message.

The message content is assumed to be Markdown. The original Markdown is stored in the message JSON file. Safe HTML rendering happens on demand for the server UI, `/chat.html`, and `export-html`.

`--wait-ms` controls how long the command retries when message files are temporarily locked. It defaults to `30000`. A value of `0` means try once and return immediately on lock failure.

On success, the command prints only the new message cursor/id to stdout and exits with code `0`.

Validation failures, unsafe role names, reserved role misuse, missing role directories, unreadable `--file` paths, and timeout failures must exit non-zero and write the error to stderr.

`--stdin`, `--file`, and inline markdown are mutually exclusive. `--file <path>` reads content from an existing local file but does not attach that file as an asset. Assets should be copied to `.simpleagentchat/assets/` first and then referenced from the Markdown.

For inline Markdown, `say` parses options only before the first Markdown token. After inline Markdown begins, every remaining token is part of the Markdown, even if it looks like an option. A `--` delimiter may be used to make the start of Markdown explicit and is required when inline Markdown itself starts with `-`.

Examples:

```powershell
dotnet simpleagentchat.cs say implementer --wait-ms 1000 hello
```

Sends `hello` with a 1000 ms wait.

```powershell
dotnet simpleagentchat.cs say implementer hello --wait-ms 1000
```

Sends the literal Markdown `hello --wait-ms 1000`.

```powershell
dotnet simpleagentchat.cs say implementer -- --not-an-option
```

Sends the literal Markdown `--not-an-option`.

Agents should use normal role names. The `say` command must reject `goal` and `system`. The `say` command should reject `human` unless a future explicit human-only option is added.

#### Reserved Roles

`goal` is reserved for tool-generated public goal-coordination messages.

`human` is reserved for human messages. The browser UI posts as `human`.

Because this is a local file-based tool, this cannot be perfectly enforced against an agent that can run arbitrary local commands. The tool should still discourage or reject casual `say human ...` use unless an explicit human-oriented option is provided.

`system` is reserved for tool-generated operational messages. Normal agents must not send messages as `goal` or `system`.

### `fetch`

```powershell
dotnet simpleagentchat.cs fetch [cursor [wait-ms]] [--wait-ms <ms>] [--json] [--include-system]
```

Returns messages after the given cursor. If no cursor is provided, it returns the initial chat context.

The initial no-cursor fetch should omit historical `system` messages by default. Those messages are operational notifications from the moment they were sent; current files are the source of truth for roles, goals, and role memory when an agent joins.

Cursor-based fetches must include newly observed `system` messages by default because agents need to obey live role, memory, and goal changes. In other words, system-message filtering is an initial-context behavior, not a normal polling behavior.

For debugging or complete transcript export, callers may request system messages explicitly:

```powershell
dotnet simpleagentchat.cs fetch --include-system
```

`wait-ms` controls how long the command waits for a new message when no output messages are available. It defaults to `30000`. A value of `0` means return immediately.

For cursor-based fetches, the wait may be supplied positionally after the cursor or with `--wait-ms <ms>`. For initial no-cursor fetches, callers must use `--wait-ms <ms>` to override the default wait. A bare first positional value is always parsed as a cursor, so `fetch 0` is invalid rather than an initial fetch with zero wait.

If both positional `wait-ms` and `--wait-ms <ms>` are provided, the command fails with a usage error.

Ordering:

- Returned messages are ordered by ascending message id.
- `nextCursor` is the newest message id observed in the underlying log, including messages filtered out of the response.
- If the log is empty, `nextCursor` is `null`.
- If no newer messages arrive before timeout, return success with `timedOut: true`, no messages, and `nextCursor` equal to the latest observed cursor.
- If an initial no-cursor fetch observes only filtered `system` messages, return immediately with `timedOut: false`, an empty `messages` array, and `nextCursor` set to the newest observed message id.

Cursor validation:

- A missing cursor means initial fetch.
- A non-empty cursor must match the message id shape.
- A syntactically valid cursor does not have to point to an existing message file; it is treated as a sort watermark.
- A syntactically invalid cursor exits non-zero.

Agents should:

1. Fetch initial context when joining.
2. Expect historical `system` messages to be filtered out of that initial no-cursor fetch by default.
3. Track the `nextCursor` returned by `fetch`.
4. Fetch from that cursor before each meaningful work step.

Agents must not advance their fetch cursor to the id returned by their own `say` command unless that message later appears in a `fetch` result. The fetch cursor means "the latest message I have fetched from the shared transcript", not "the latest message I personally sent".

Even when messages are filtered from fetch output, the fetch response must include a next cursor or watermark representing the newest message observed in the underlying log. This prevents old filtered `system` messages from appearing as new messages on the next fetch.

`--json` is the stable machine-readable contract and should be preferred by agents:

```powershell
dotnet simpleagentchat.cs fetch [cursor [wait-ms]] [--wait-ms <ms>] --json
```

JSON response schema:

```json
{
  "nextCursor": "20260704T124000.0000000Z-system-d4e5f6",
  "timedOut": false,
  "messages": [
    {
      "id": "20260704T123456.1234567Z-implementer-a1b2c3",
      "timestampUtc": "2026-07-04T12:34:56.1234567Z",
      "role": "implementer",
      "kind": "chat.message",
      "markdown": "I can take the first implementation pass.",
      "changedPaths": []
    }
  ]
}
```

When no messages are returned:

```json
{
  "nextCursor": "20260704T124000.0000000Z-system-d4e5f6",
  "timedOut": true,
  "messages": []
}
```

Plain-text output must be stable enough for humans and simple agents:

```text
nextCursor: 20260704T124000.0000000Z-system-d4e5f6
timedOut: false
messageCount: 1

--- message 20260704T123456.1234567Z-implementer-a1b2c3
timestampUtc: 2026-07-04T12:34:56.1234567Z
role: implementer
kind: chat.message
markdown:
I can take the first implementation pass.
--- end
```

When a command fails, JSON mode should return a JSON error object on stderr:

```json
{
  "error": {
    "code": "invalid_cursor",
    "message": "Cursor is not a valid simpleagentchat message id."
  }
}
```

### `export-html`

```powershell
dotnet simpleagentchat.cs export-html
```

Generates `.simpleagentchat/chat.html` from the canonical message files, prints the generated file path, and exits with code `0`.

This command is for read-only sharing or offline inspection. Normal `serve`, `say`, `goal`, and UI workflows should not regenerate `chat.html`; the live browser UI should render messages through the local server message API instead.

### `goal`

```powershell
dotnet simpleagentchat.cs goal done <role> <goal_file_name> [--wait-ms <ms>]
dotnet simpleagentchat.cs goal undone <role> <goal_file_name> [--wait-ms <ms>]
dotnet simpleagentchat.cs goal status <goal_file_name> [--json]
dotnet simpleagentchat.cs goal recheck <goal_file_name> [--wait-ms <ms>] <reason till end of line>
```

The `goal` command records and reports public completion agreement for goal files in `.simpleagentchat/goals/`.

Validation:

- `<goal_file_name>` must be a safe goal file name and must exist in `.simpleagentchat/goals/`.
- `<role>` must be a safe role name and must have a current role directory.
- `goal` is a reserved role name and cannot be used as an agent role.
- `reason` for `goal recheck` must be non-empty after trimming whitespace.

`goal done <role> <goal_file_name>` marks that role's status for the goal as `done`, writes the updated status file, appends a public message from `<role>` with kind `goals.done`, prints the new message cursor/id to stdout, and exits with code `0`.

`goal undone <role> <goal_file_name>` marks that role's status for the goal as `undone`, writes the updated status file, appends a public message from `<role>` with kind `goals.undone`, prints the new message cursor/id to stdout, and exits with code `0`.

`goal status <goal_file_name>` reports the current status for every current valid role. Roles with no explicit entry are reported as `undone`. The goal is complete only when every current valid role is `done`.

When a role is implicitly `undone` because it has no explicit status entry yet, plain-text output omits the timestamp for that role and JSON output uses `null` for `updatedAtUtc` and `messageId`.

Plain-text status output must be stable:

```text
goal: release.md
complete: false
roleCount: 2
implementer: done 2026-07-04T12:34:56.1234567Z
reviewer: undone 2026-07-04T12:40:00.0000000Z
```

With `--json`, status output should use this shape:

```json
{
  "goal": "release.md",
  "complete": false,
  "roles": [
    {
      "role": "implementer",
      "status": "done",
      "updatedAtUtc": "2026-07-04T12:34:56.1234567Z",
      "messageId": "20260704T123456.1234567Z-implementer-a1b2c3"
    },
    {
      "role": "reviewer",
      "status": "undone",
      "updatedAtUtc": "2026-07-04T12:40:00.0000000Z",
      "messageId": "20260704T124000.0000000Z-reviewer-d4e5f6"
    }
  ]
}
```

`goal recheck <goal_file_name> <reason>` is for important changes that invalidate previous completion agreement. It must:

1. Mark the target goal `undone` for every current valid role.
2. Write the updated status file.
3. Append a `system` message with kind `goals.recheck` explaining that all roles must re-check and re-approve the goal.
4. Append a regular public chat message from the reserved `goal` role with kind `chat.message` and the supplied reason.
5. Print both created message cursor ids to stdout in a stable form.

Example `goal recheck` stdout:

```text
systemCursor: 20260704T124000.0000000Z-system-d4e5f6
messageCursor: 20260704T124000.0000001Z-goal-a1b2c3
```

For `goal recheck`, options are parsed only before the first reason token. After the reason begins, every remaining token is part of the reason, even if it looks like an option. A `--` delimiter may be used to make the start of the reason explicit and is required when the reason itself starts with `-`.

## Message Model

Each message must be represented by a JSON file with this shape:

```json
{
  "id": "20260704T123456.1234567Z-implementer-a1b2c3",
  "timestampUtc": "2026-07-04T12:34:56.1234567Z",
  "role": "implementer",
  "kind": "chat.message",
  "markdown": "I can take the first implementation pass.",
  "changedPaths": []
}
```

System messages must use the same shape and may include structured change information:

```json
{
  "id": "20260704T124000.0000000Z-system-d4e5f6",
  "timestampUtc": "2026-07-04T12:40:00.0000000Z",
  "role": "system",
  "kind": "roles.changed",
  "markdown": "Roles changed. Agents must re-read `.simpleagentchat/roles` before continuing.",
  "changedPaths": [
    ".simpleagentchat/roles/implementer/instructions.md"
  ]
}
```

Required fields:

- `id`: canonical message id and cursor.
- `timestampUtc`: UTC timestamp in round-trip ISO-8601 form.
- `role`: safe role name, `human`, `system`, or the reserved tool role `goal`.
- `kind`: machine-readable message kind such as `chat.message`, `roles.changed`, `roles.memory.changed`, `roles.deleted`, `goals.changed`, `goals.done`, `goals.undone`, or `goals.recheck`.
- `markdown`: original Markdown content.
- `changedPaths`: root-relative changed paths, or an empty array.

Message ids are cursors. They must be sortable in chronological order and unique even when multiple messages arrive in the same clock tick.

## Markdown Rendering

Messages are authored as Markdown and rendered to safe HTML for the live server UI, the on-demand `/chat.html` view, and exported `chat.html` transcripts.

The renderer should escape unsafe HTML by default. Raw HTML in Markdown should either be disabled or treated as a trusted explicit mode.

Implementation options:

- built-in minimal Markdown renderer for maximum portability
- optional Markdig-based renderer for richer Markdown if the single-file app can restore NuGet dependencies cleanly

For v1, portability and safety matter more than complete Markdown support.

## System Messages

`system` messages are generated by the tool when operational state changes.

Examples:

```text
Roles changed. Agents must re-read `.simpleagentchat/roles` before continuing.
! Role "implementer" was deleted. Any agent using that role must stop immediately.
Goals changed. Agents must re-read `.simpleagentchat/goals` before continuing.
```

Rules:

- Role edits generate a `roles.changed` system message.
- Role deletion generates a critical `!` system message.
- Human/UI edits to role memory generate a `roles.memory.changed` system message.
- Agent-authored updates to that agent's own role memory may be written directly to the filesystem and do not need a `system` message.
- Goal edits generate a `goals.changed` system message.
- Goal deletion generates a `goals.changed` system message.
- `goal done` and `goal undone` append public non-system goal-status messages from the approving or withdrawing role.
- `goal recheck` generates a `goals.recheck` system message and a regular public `goal` chat message.
- Agents must obey newly fetched `system` messages before continuing work.
- Initial no-cursor fetches omit historical `system` messages by default. Current role files, goal files, and role memory are authoritative on join.
- Agents whose role directory no longer exists must stop working.

## Joining and Rejoining as a Role

Agents do not treat every session as a blank slate. A role has continuity across sessions through chat history and `role_memory.md`.

When joining a chat as a role, an agent must:

1. Fetch initial context with no cursor.
2. Use the returned `nextCursor` as its fetched cursor, even if some messages were filtered from the initial output.
3. Read all current goals.
4. Read `.simpleagentchat/roles/<role>/instructions.md`.
5. Read `.simpleagentchat/roles/<role>/role_memory.md`.
6. Review prior non-system messages from the same role to understand what that role did, tried, decided, or promised.
7. Continue in character as that role.

If the fetched history already contains an explicit human `Start`, the joining agent should not wait for another `Start`. It should resume from the existing chat state and the role's prior activity.

If no explicit human `Start` appears in the fetched history, agents may discuss, ask clarifying questions, or prepare, but must not begin implementation work until the human says `Start`.

Agents should update their own `role_memory.md` directly on the filesystem when they learn something that would help a future session of the same role. These self-updates do not need to emit `system` messages. Keep these notes concise and durable. Do not use role memory as a substitute for public chat messages when other participants need to know something now.

## Start and Critical Message Semantics

A `Start` signal is machine-checkable:

- The message role must be `human`.
- The message kind must be `chat.message`.
- The Markdown content, after trimming leading and trailing whitespace, must equal `Start` using case-insensitive ASCII comparison.
- `Start` embedded in a longer sentence does not count.

Examples that count:

```text
Start
start
  START
```

Examples that do not count:

```text
Let's start
Start please
```

A critical message is machine-checkable:

- The Markdown content starts with `!` after trimming leading whitespace.
- Critical messages may come from `human`, `system`, or an agent role.
- Agents must treat newly fetched critical messages as immediate blockers until they have obeyed or addressed them.
- The UI critical-message control should prepend `! ` if the human did not type it.

Historical non-system critical messages remain visible in the initial fetch. Historical `system` messages are filtered from initial no-cursor fetches by default.

## Goal Completion Etiquette

Goals are not considered complete because one role says they are complete. A goal is complete only when every current valid role has publicly marked that goal `done`.

Agents should use:

```powershell
dotnet simpleagentchat.cs goal done <role> <goal_file_name>
```

only after that role has actually checked the goal and believes it is complete from that role's responsibility.

Agreement can be withdrawn at any time:

```powershell
dotnet simpleagentchat.cs goal undone <role> <goal_file_name>
```

Roles should withdraw agreement if newer chat messages, role changes, goal edits, code changes, review findings, test failures, or other new evidence invalidates the previous completion judgment.

Agents should check completion status before saying a goal is done:

```powershell
dotnet simpleagentchat.cs goal status <goal_file_name>
```

When important changes require every role to re-check the goal, any participant may request a recheck:

```powershell
dotnet simpleagentchat.cs goal recheck <goal_file_name> <reason>
```

Recheck should be used with care because it resets all current role approvals for the target goal to `undone`. If important changes have been made, though, requesting a recheck is required so stale approvals do not make the goal look complete.

## Chat Etiquette

These rules belong in `HOW_TO_CHAT.md` and should be followed by all agents:

- Fetch all previous context before joining.
- Initial no-cursor fetches omit historical `system` messages by default; use current files as the source of truth.
- Preserve the `nextCursor` returned by initial fetch even when filtered messages are omitted.
- Read all goals before starting work.
- Use `goal status <goal_file_name>` before claiming a goal is complete.
- A goal is complete only when every current valid role has publicly marked it `done`.
- Use `goal done <role> <goal_file_name>` only after checking the goal from your role's responsibility.
- Use `goal undone <role> <goal_file_name>` when new evidence invalidates your previous completion agreement.
- Use `goal recheck <goal_file_name> <reason>` when important changes require every role to re-check and re-approve the goal.
- Read your assigned role instructions and role memory before speaking or working.
- Review what your role previously said or attempted, then continue from there.
- Do not begin implementation work until the human explicitly says `Start`, unless a prior `Start` already exists in fetched chat history.
- After joining, always fetch from your latest fetched cursor before each meaningful work step.
- Do not advance your fetch cursor from your own `say` result. Advance it only from messages returned by `fetch`.
- Critical or blocker messages are prefixed with `!`.
- Always obey critical `!` messages immediately.
- Always obey newly fetched `system` messages immediately.
- If your role instructions or role memory change because of a newly fetched `system` message, re-read them before continuing.
- If your role directory is deleted, stop working.
- Keep role memory concise and update it directly only with durable context that will help future sessions of the same role.
- Do not use the `goal` role unless you are the tool.
- Do not use the `human` role unless you are the human.
- Do not use the `system` role unless you are the tool.
- All messages are public.

## Concurrency and File Safety

The implementation should assume multiple agents may call `say` and `fetch` concurrently.

Required approach:

- Write each message to a temporary file first.
- Flush and close the file.
- Atomically move it into `.simpleagentchat/messages/`.
- Use retry loops for temporary file locks.
- Avoid editing existing message files.
- Sort messages by cursor/id when reading.
- Write goal status files through temporary files and atomic replacement.

The explicit `export-html` command should write `chat.html` through a temporary file and atomically replace the prior export.

## Local Server

The local server exists so humans can use a browser UI.

It must:

- bind to loopback only, either `127.0.0.1` or `localhost`
- avoid external network dependencies
- serve the UI, a live message API, and an on-demand read-only chat view
- write all messages through the same message creation path as `say`
- generate `system` messages for human/UI role and goal changes
- notify browser clients when message, role, goal, or asset files change
- reject any request that resolves outside the allowed `.simpleagentchat/` subdirectory

Port behavior:

- If `--port <port>` is supplied and unavailable, `serve` exits non-zero.
- If no port is supplied, try `8765`, then increment until an available port is found through `8799`.
- If no port in that range is available, exit non-zero.

Browser behavior:

- By default, `serve` tries to open the UI URL in the default browser.
- If opening the browser fails, `serve` prints the URL and continues serving.
- With `--no-open`, `serve` prints the URL and does not attempt to open a browser.

Required endpoints:

```text
GET  /
GET  /chat.html
GET  /api/messages?cursor=<cursor>&waitMs=<ms>&includeSystem=<bool>
GET  /api/events
POST /api/messages
GET  /api/roles
POST /api/roles
GET  /api/roles/<role>
PUT  /api/roles/<role>/instructions
PUT  /api/roles/<role>/memory
POST /api/roles/<role>/rename
DELETE /api/roles/<role>
GET  /api/goals
POST /api/goals
GET  /api/goals/<name>
PUT  /api/goals/<name>
POST /api/goals/<name>/rename
DELETE /api/goals/<name>
GET  /api/assets
GET  /assets/<name>
PUT  /api/assets/<name>
DELETE /api/assets/<name>
```

Endpoint contracts:

- `GET /` serves `ui.html`.
- `GET /chat.html` serves an on-demand read-only transcript without writing `.simpleagentchat/chat.html`.
- `GET /api/messages` returns the same core JSON schema as `fetch --json`, with an additional `html` field on each message for safe browser rendering. For UI use and cursor-based polling, `includeSystem` should default to `true`; for no-cursor agent CLI fetches it defaults to `false`.
- `GET /api/events` returns a Server-Sent Events stream that notifies browser clients when messages, roles, goals, or assets change. The server should watch the chat files so messages written by agent CLI commands refresh the browser UI without manual polling.
- `POST /api/messages` accepts `{ "markdown": "...", "critical": false }`, writes a `human` message, and returns the created message object plus `nextCursor`. If `critical` is `true` and the trimmed Markdown does not start with `!`, the server prepends `! ` before writing the message.
- `GET /api/roles` returns role names and metadata for valid role directories.
- `POST /api/roles` accepts `{ "role": "...", "instructions": "...", "memory": "..." }`, creates a new role, rejects existing roles, and emits a `roles.changed` system message.
- `GET /api/roles/<role>` returns `{ "role": "...", "instructions": "...", "memory": "..." }`.
- `PUT /api/roles/<role>/instructions` accepts `{ "markdown": "..." }`, creates the role directory and default `role_memory.md` if the role does not already exist, writes `instructions.md`, and emits a `roles.changed` system message.
- `PUT /api/roles/<role>/memory` accepts `{ "markdown": "..." }`, writes `role_memory.md`, and emits a `roles.memory.changed` system message because the edit came through the human/UI channel.
- `POST /api/roles/<role>/rename` accepts `{ "role": "new-role" }`, renames the current role, rejects existing target roles, preserves instructions and memory, updates goal status metadata for the renamed role, and emits a `roles.changed` system message.
- `DELETE /api/roles/<role>` deletes that role directory and emits a critical `roles.deleted` system message whose Markdown starts with `!`.
- `GET /api/goals` returns safe goal file names and metadata.
- `POST /api/goals` accepts `{ "name": "...", "content": "..." }`, creates a new goal, rejects existing goals, resets completion status for all current roles, and emits a `goals.changed` system message.
- `GET /api/goals/<name>` returns `{ "name": "...", "content": "..." }`.
- `PUT /api/goals/<name>` accepts `{ "content": "..." }`, writes the goal file, resets completion status for that goal to `undone` for all current roles, and emits a `goals.changed` system message.
- `POST /api/goals/<name>/rename` accepts `{ "name": "new-name.md" }`, renames the current goal, rejects existing target goals, preserves the goal content and status metadata, and emits a `goals.changed` system message.
- `DELETE /api/goals/<name>` deletes the goal file, deletes the goal status file if present, and emits a `goals.changed` system message.
- `GET /api/assets` returns safe asset file names and metadata.
- `GET /assets/<name>` serves only a safe file from `.simpleagentchat/assets/`.
- `PUT /api/assets/<name>` writes raw request bytes to `.simpleagentchat/assets/<name>`.
- `DELETE /api/assets/<name>` deletes a safe asset file from `.simpleagentchat/assets/`.

Asset boundaries:

- Asset names must pass the safe asset filename rules.
- v1 asset upload should reject files larger than `25 MiB`.
- v1 should not unpack archives or create nested directories.
- Assets should be served with conservative content types. Unknown types should use `application/octet-stream`.
- Asset responses must include `X-Content-Type-Options: nosniff`.
- The server must not serve uploaded active content as executable same-origin browser content. Files with active or script-like extensions such as `.html`, `.htm`, `.svg`, `.xml`, `.js`, `.mjs`, and `.css` must be served as `application/octet-stream` or inert text with `Content-Disposition: attachment`.
- Passive assets such as `.txt`, `.md`, `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, and `.pdf` may be served inline with conservative content types.

The server is not the canonical store. Files are.

## Security and Trust Boundaries

This is a local collaboration tool, not a secure multi-tenant system.

Important limits:

- Agents with shell access can read and modify `.simpleagentchat/`.
- The `human` role cannot be cryptographically protected without extra local identity or authentication machinery.
- Raw HTML in messages can create browser risks and should be escaped by default.
- The server should bind only to localhost.
- The server should avoid serving arbitrary files outside `.simpleagentchat/` except where explicitly designed.

The tool should be honest about these boundaries in docs.

## Acceptance Criteria for v1

A successful v1 should support:

- Copy `simpleagentchat.cs` into a repository.
- Run `dotnet simpleagentchat.cs serve`.
- Running `serve` or `init` outside a Git repository fails without creating files.
- Missing chat folders and instruction files are created.
- `.simpleagentchat/` is added to `.gitignore`.
- Re-running initialization does not duplicate `.gitignore` entries or instruction blocks.
- Unsafe role, goal, asset, and cursor inputs are rejected without touching files outside the allowed directories.
- The browser UI opens or a local URL is printed.
- The server exposes the required local endpoints and binds only to loopback.
- The human can add and edit goals.
- The human can add and edit roles.
- The human can view and edit role memory/thoughts files.
- The human can upload and view safe asset files.
- Role and goal changes produce visible `system` messages.
- Human/UI role memory changes produce visible `system` messages.
- Agent-authored direct role memory updates do not need to produce `system` messages.
- The human can send a `human` message from the UI.
- An agent can send a Markdown message with `say`.
- `say` returns the new message cursor.
- `fetch` returns initial context without historical `system` messages when called without a cursor.
- Cursor-based `fetch` includes newly observed `system` messages by default.
- `fetch` returns a `nextCursor` watermark even when messages are filtered from output.
- `fetch --json` returns the specified JSON schema for messages, timeouts, and errors.
- `fetch <cursor> <wait-ms>` returns newer messages or waits briefly.
- `goal done <role> <goal_file_name>` records public completion agreement for that role.
- `goal undone <role> <goal_file_name>` withdraws that role's completion agreement.
- `goal status <goal_file_name>` reports status for every current valid role and reports complete only when all are `done`.
- `goal recheck <goal_file_name> <reason>` resets all current role approvals for the target goal and emits both a system message and a regular public goal message.
- `export-html` creates `.simpleagentchat/chat.html`, and that file renders the Markdown chat transcript with roles and timestamps.
- Deleting a role produces a critical system message telling affected agents to stop.
- Rejoining agents can determine from fetched history whether `Start` already happened and can resume the role from its previous messages and memory.

## Possible Later Enhancements

- Named multiple chats per repository.
- Stronger human identity confirmation.
- Richer Markdown via Markdig.
- Asset upload improvements.
- Message search.
- Per-agent local cursor files.
- Export transcript to Markdown.
- Optional browser notifications.
- Optional `watch` or long-running agent helper mode.
