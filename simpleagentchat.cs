#if !SIMPLEAGENTCHAT_TEST
return await SimpleAgentChat.Program.MainAsync(args);
#endif

#pragma warning disable IL2026, IL3050

namespace SimpleAgentChat
{

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    public static async Task<int> MainAsync(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                throw CliException.Usage("usage", "Usage: dotnet simpleagentchat.cs <init|serve|say|fetch|goal> ...");
            }

            ErrorWriter.JsonMode = WantsJsonErrors(args);
            var command = args[0];
            var rest = args.Skip(1).ToArray();
            return command switch
            {
                "init" => await Commands.InitAsync(rest),
                "say" => await Commands.SayAsync(rest),
                "fetch" => await Commands.FetchAsync(rest),
                "goal" => await Commands.GoalAsync(rest),
                "serve" => await Commands.ServeAsync(rest),
                _ => throw CliException.Usage("unknown_command", $"Unknown command '{command}'.")
            };
        }
        catch (CliException ex)
        {
            ErrorWriter.Write(ex);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            ErrorWriter.Write(new CliException(1, "unexpected_error", ex.Message));
            return 1;
        }
    }

    private static bool WantsJsonErrors(string[] args)
    {
        return args.Length > 1 &&
               (args[0] == "fetch" || args[0] == "goal") &&
               args.Skip(1).Any(arg => arg == "--json");
    }
}

internal static class Commands
{
    public static Task<int> InitAsync(string[] args)
    {
        if (args.Length != 0)
        {
            throw CliException.Usage("usage", "Usage: dotnet simpleagentchat.cs init");
        }

        var root = RepositoryRoot.FindRequired();
        ChatWorkspace.Initialize(root);
        return Task.FromResult(0);
    }

    public static async Task<int> SayAsync(string[] args)
    {
        var parsed = SayArgs.Parse(args);
        var root = RepositoryRoot.FindRequired();
        var workspace = ChatWorkspace.OpenExisting(root);

        if (!NameRules.IsValidRoleName(parsed.Role, allowReserved: false))
        {
            throw CliException.Validation("invalid_role", "Role is not a valid current agent role name.");
        }

        var roleDir = workspace.RoleDirectory(parsed.Role);
        if (!Directory.Exists(roleDir))
        {
            throw CliException.Validation("missing_role", $"Role '{parsed.Role}' does not exist.");
        }

        var markdown = await parsed.ReadMarkdownAsync();
        var store = new MessageStore(workspace);
        var message = await store.AppendAsync(parsed.Role, "chat.message", markdown, Array.Empty<string>(), parsed.WaitMs);
        Console.Out.WriteLine(message.Id);
        return 0;
    }

    public static async Task<int> FetchAsync(string[] args)
    {
        var parsed = FetchArgs.Parse(args);
        ErrorWriter.JsonMode = parsed.Json;
        var root = RepositoryRoot.FindRequired();
        var workspace = ChatWorkspace.OpenExisting(root);
        var store = new MessageStore(workspace);
        var response = await store.FetchAsync(parsed.Cursor, parsed.IncludeSystem, parsed.WaitMs);

        if (parsed.Json)
        {
            Console.Out.WriteLine(Json.Text(response));
        }
        else
        {
            PlainText.WriteFetch(response);
        }

        return 0;
    }

    public static async Task<int> GoalAsync(string[] args)
    {
        var parsed = GoalArgs.Parse(args);
        ErrorWriter.JsonMode = parsed.Json;
        var root = RepositoryRoot.FindRequired();
        var workspace = ChatWorkspace.OpenExisting(root);
        var goals = new GoalStatusStore(workspace);

        switch (parsed.Action)
        {
            case "status":
            {
                var report = goals.GetStatus(parsed.GoalName!);
                if (parsed.Json)
                {
                    Console.Out.WriteLine(Json.Text(report));
                }
                else
                {
                    PlainText.WriteGoalStatus(report);
                }

                return 0;
            }
            case "done":
            case "undone":
            {
                var message = await goals.MarkRoleStatusAsync(parsed.GoalName!, parsed.Role!, parsed.Action, parsed.WaitMs);
                Console.Out.WriteLine(message.Id);
                return 0;
            }
            case "recheck":
            {
                var messages = await goals.RecheckAsync(parsed.GoalName!, parsed.Reason!, parsed.WaitMs);
                Console.Out.WriteLine($"systemCursor: {messages.SystemMessage.Id}");
                Console.Out.WriteLine($"messageCursor: {messages.GoalMessage.Id}");
                return 0;
            }
            default:
                throw CliException.Usage("usage", "Unknown goal subcommand.");
        }
    }

    public static async Task<int> ServeAsync(string[] args)
    {
        var parsed = ServeArgs.Parse(args);
        var root = RepositoryRoot.FindRequired();
        var workspace = ChatWorkspace.Initialize(root);
        var server = new LocalServer(workspace, parsed.Port, parsed.NoOpen);
        return await server.RunAsync();
    }
}

internal sealed class CliException : Exception
{
    public int ExitCode { get; }
    public string Code { get; }

    public CliException(int exitCode, string code, string message)
        : base(message)
    {
        ExitCode = exitCode;
        Code = code;
    }

    public static CliException Usage(string code, string message) => new(1, code, message);
    public static CliException Validation(string code, string message) => new(1, code, message);
    public static CliException LockTimeout(string message) => new(2, "lock_timeout", message);
    public static CliException ServerStartup(string message) => new(3, "server_startup", message);
}

internal static class ErrorWriter
{
    public static bool JsonMode { get; set; }

    public static void Write(CliException ex)
    {
        if (JsonMode)
        {
            var body = new
            {
                error = new
                {
                    code = ex.Code,
                    message = ex.Message
                }
            };
            Console.Error.WriteLine(Json.Text(body));
        }
        else
        {
            Console.Error.WriteLine(ex.Message);
        }
    }
}

internal sealed record SayArgs(string Role, int WaitMs, string? InlineMarkdown, bool UseStdin, string? FilePath)
{
    public static SayArgs Parse(string[] args)
    {
        if (args.Length < 1)
        {
            throw CliException.Usage("usage", "Usage: dotnet simpleagentchat.cs say <role> [--wait-ms <ms>] <markdown>|--stdin|--file <path>");
        }

        var role = args[0];
        var waitMs = 30000;
        string? inline = null;
        string? file = null;
        var stdin = false;
        var i = 1;
        var markdownStarted = false;

        while (i < args.Length)
        {
            var token = args[i];
            if (!markdownStarted && token == "--")
            {
                inline = string.Join(" ", args.Skip(i + 1));
                markdownStarted = true;
                break;
            }

            if (!markdownStarted && token == "--wait-ms")
            {
                if (i + 1 >= args.Length)
                {
                    throw CliException.Usage("usage", "--wait-ms requires a value.");
                }

                waitMs = ParseWaitMs(args[i + 1]);
                i += 2;
                continue;
            }

            if (!markdownStarted && token == "--stdin")
            {
                stdin = true;
                i++;
                continue;
            }

            if (!markdownStarted && token == "--file")
            {
                if (i + 1 >= args.Length)
                {
                    throw CliException.Usage("usage", "--file requires a path.");
                }

                file = args[i + 1];
                i += 2;
                continue;
            }

            inline = string.Join(" ", args.Skip(i));
            markdownStarted = true;
            break;
        }

        var sources = 0;
        if (inline is not null) sources++;
        if (stdin) sources++;
        if (file is not null) sources++;
        if (sources != 1)
        {
            throw CliException.Usage("usage", "Exactly one of inline Markdown, --stdin, or --file is required.");
        }

        return new SayArgs(role, waitMs, inline, stdin, file);
    }

    public async Task<string> ReadMarkdownAsync()
    {
        if (UseStdin)
        {
            return await Console.In.ReadToEndAsync();
        }

        if (FilePath is not null)
        {
            try
            {
                return await File.ReadAllTextAsync(FilePath, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw CliException.Validation("unreadable_file", $"Could not read --file path: {ex.Message}");
            }
        }

        return InlineMarkdown ?? "";
    }

    private static int ParseWaitMs(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var waitMs) || waitMs < 0)
        {
            throw CliException.Validation("invalid_wait_ms", "wait-ms must be an integer from 0 through 2147483647.");
        }

        return waitMs;
    }
}

internal sealed record FetchArgs(string? Cursor, int WaitMs, bool Json, bool IncludeSystem)
{
    public static FetchArgs Parse(string[] args)
    {
        string? cursor = null;
        int? waitMs = null;
        int? positionalWaitMs = null;
        var json = false;
        bool? includeSystem = null;
        var positionals = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                json = true;
                continue;
            }

            if (token == "--include-system")
            {
                includeSystem = true;
                continue;
            }

            if (token == "--wait-ms")
            {
                if (i + 1 >= args.Length)
                {
                    throw CliException.Usage("usage", "--wait-ms requires a value.");
                }

                waitMs = ParseWaitMs(args[++i]);
                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                throw CliException.Usage("usage", $"Unknown option '{token}'.");
            }

            positionals.Add(token);
        }

        if (positionals.Count > 2)
        {
            throw CliException.Usage("usage", "Usage: dotnet simpleagentchat.cs fetch [cursor [wait-ms]] [--wait-ms <ms>] [--json] [--include-system]");
        }

        if (positionals.Count >= 1)
        {
            cursor = positionals[0];
            if (!NameRules.IsValidMessageId(cursor))
            {
                throw CliException.Validation("invalid_cursor", "Cursor is not a valid simpleagentchat message id.");
            }
        }

        if (positionals.Count == 2)
        {
            positionalWaitMs = ParseWaitMs(positionals[1]);
        }

        if (waitMs.HasValue && positionalWaitMs.HasValue)
        {
            throw CliException.Usage("usage", "wait-ms cannot be supplied both positionally and with --wait-ms.");
        }

        var finalWaitMs = waitMs ?? positionalWaitMs ?? 30000;
        var finalIncludeSystem = includeSystem ?? (cursor is not null);
        return new FetchArgs(cursor, finalWaitMs, json, finalIncludeSystem);
    }

    private static int ParseWaitMs(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var waitMs) || waitMs < 0)
        {
            throw CliException.Validation("invalid_wait_ms", "wait-ms must be an integer from 0 through 2147483647.");
        }

        return waitMs;
    }
}

internal sealed record GoalArgs(string Action, string? Role, string? GoalName, int WaitMs, bool Json, string? Reason)
{
    public static GoalArgs Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw CliException.Usage("usage", "Usage: dotnet simpleagentchat.cs goal <status|done|undone|recheck> ...");
        }

        var action = args[0];
        return action switch
        {
            "status" => ParseStatus(args.Skip(1).ToArray()),
            "done" => ParseMark("done", args.Skip(1).ToArray()),
            "undone" => ParseMark("undone", args.Skip(1).ToArray()),
            "recheck" => ParseRecheck(args.Skip(1).ToArray()),
            _ => throw CliException.Usage("usage", $"Unknown goal subcommand '{action}'.")
        };
    }

    private static GoalArgs ParseStatus(string[] args)
    {
        var json = false;
        string? goal = null;
        foreach (var token in args)
        {
            if (token == "--json")
            {
                json = true;
                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                throw CliException.Usage("usage", $"Unknown option '{token}'.");
            }

            if (goal is not null)
            {
                throw CliException.Usage("usage", "Usage: dotnet simpleagentchat.cs goal status <goal_file_name> [--json]");
            }

            goal = token;
        }

        if (goal is null)
        {
            throw CliException.Usage("usage", "goal status requires a goal file name.");
        }

        return new GoalArgs("status", null, goal, 0, json, null);
    }

    private static GoalArgs ParseMark(string action, string[] args)
    {
        var waitMs = 30000;
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--wait-ms")
            {
                if (i + 1 >= args.Length)
                {
                    throw CliException.Usage("usage", "--wait-ms requires a value.");
                }

                waitMs = CliParsing.ParseWaitMs(args[++i]);
                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                throw CliException.Usage("usage", $"Unknown option '{token}'.");
            }

            positionals.Add(token);
        }

        if (positionals.Count != 2)
        {
            throw CliException.Usage("usage", $"Usage: dotnet simpleagentchat.cs goal {action} <role> <goal_file_name> [--wait-ms <ms>]");
        }

        return new GoalArgs(action, positionals[0], positionals[1], waitMs, false, null);
    }

    private static GoalArgs ParseRecheck(string[] args)
    {
        var waitMs = 30000;
        string? goal = null;
        string? reason = null;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (reason is null && token == "--")
            {
                reason = string.Join(" ", args.Skip(i + 1));
                break;
            }

            if (reason is null && token == "--wait-ms")
            {
                if (i + 1 >= args.Length)
                {
                    throw CliException.Usage("usage", "--wait-ms requires a value.");
                }

                waitMs = CliParsing.ParseWaitMs(args[++i]);
                continue;
            }

            if (goal is null)
            {
                if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    throw CliException.Usage("usage", $"Unknown option '{token}'.");
                }

                goal = token;
                continue;
            }

            reason = string.Join(" ", args.Skip(i));
            break;
        }

        if (goal is null)
        {
            throw CliException.Usage("usage", "goal recheck requires a goal file name.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw CliException.Validation("empty_reason", "goal recheck reason must be non-empty.");
        }

        return new GoalArgs("recheck", null, goal, waitMs, false, reason);
    }
}

internal sealed record ServeArgs(int? Port, bool NoOpen)
{
    public static ServeArgs Parse(string[] args)
    {
        int? port = null;
        var noOpen = false;
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--no-open")
            {
                noOpen = true;
                continue;
            }

            if (token == "--port")
            {
                if (i + 1 >= args.Length)
                {
                    throw CliException.Usage("usage", "--port requires a value.");
                }

                if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) ||
                    parsedPort < 1 ||
                    parsedPort > 65535)
                {
                    throw CliException.Validation("invalid_port", "Port must be an integer from 1 through 65535.");
                }

                port = parsedPort;
                continue;
            }

            throw CliException.Usage("usage", $"Unknown option '{token}'.");
        }

        return new ServeArgs(port, noOpen);
    }
}

internal static class CliParsing
{
    public static int ParseWaitMs(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var waitMs) || waitMs < 0)
        {
            throw CliException.Validation("invalid_wait_ms", "wait-ms must be an integer from 0 through 2147483647.");
        }

        return waitMs;
    }
}

internal static partial class NameRules
{
    private static readonly Regex RoleRegex = new("^[a-z][a-z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FileNameRegex = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MessageIdRegex = new("^\\d{8}T\\d{6}\\.\\d{7}Z-[a-z][a-z0-9_-]{0,63}-[a-f0-9]{6,16}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReservedRoles = new(StringComparer.OrdinalIgnoreCase) { "goal", "human", "system" };
    private static readonly HashSet<string> WindowsDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsValidRoleName(string? role, bool allowReserved)
    {
        if (string.IsNullOrWhiteSpace(role) || !RoleRegex.IsMatch(role))
        {
            return false;
        }

        if (!allowReserved && ReservedRoles.Contains(role))
        {
            return false;
        }

        return !IsWindowsDeviceName(role);
    }

    public static bool IsValidMessageRole(string? role)
    {
        return role is "human" or "system" or "goal" || IsValidRoleName(role, allowReserved: false);
    }

    public static bool IsValidGoalOrAssetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!FileNameRegex.IsMatch(name))
        {
            return false;
        }

        if (name.StartsWith(".", StringComparison.Ordinal) ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.EndsWith(".", StringComparison.Ordinal) ||
            name.EndsWith(" ", StringComparison.Ordinal))
        {
            return false;
        }

        if (name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        if (name.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        return !IsWindowsDeviceName(name);
    }

    public static bool IsValidMessageId(string? id) => id is not null && MessageIdRegex.IsMatch(id);

    private static bool IsWindowsDeviceName(string value)
    {
        var baseName = value;
        var dotIndex = value.IndexOf('.');
        if (dotIndex >= 0)
        {
            baseName = value[..dotIndex];
        }

        return WindowsDevices.Contains(baseName);
    }
}

internal static class RepositoryRoot
{
    public static string FindRequired()
    {
        var gitRoot = TryGitRoot();
        if (gitRoot is not null)
        {
            return gitRoot;
        }

        var fallback = WalkForGitDirectory(Environment.CurrentDirectory);
        if (fallback is not null)
        {
            return fallback;
        }

        throw CliException.Validation("not_git_repository", "No Git repository root found. simpleagentchat v1 only initializes inside a Git repository.");
    }

    private static string? TryGitRoot()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --show-toplevel",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0)
            {
                return null;
            }

            var path = output.Trim();
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? WalkForGitDirectory(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current is not null)
        {
            var gitDir = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitDir) || File.Exists(gitDir))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}

internal sealed class ChatWorkspace
{
    public string Root { get; }
    public string ChatDir { get; }
    public string AssetsDir { get; }
    public string GoalsDir { get; }
    public string GoalStatusDir { get; }
    public string MessagesDir { get; }
    public string RolesDir { get; }
    public string StatePath { get; }
    public string ChatHtmlPath { get; }
    public string UiHtmlPath { get; }
    public string GitIgnorePath { get; }
    public string HowToChatPath { get; }
    public string AgentsPath { get; }

    private ChatWorkspace(string root)
    {
        Root = Path.GetFullPath(root);
        ChatDir = Path.Combine(Root, ".simpleagentchat");
        AssetsDir = Path.Combine(ChatDir, "assets");
        GoalsDir = Path.Combine(ChatDir, "goals");
        GoalStatusDir = Path.Combine(ChatDir, "goal_status");
        MessagesDir = Path.Combine(ChatDir, "messages");
        RolesDir = Path.Combine(ChatDir, "roles");
        StatePath = Path.Combine(ChatDir, "state.json");
        ChatHtmlPath = Path.Combine(ChatDir, "chat.html");
        UiHtmlPath = Path.Combine(ChatDir, "ui.html");
        GitIgnorePath = Path.Combine(Root, ".gitignore");
        HowToChatPath = Path.Combine(Root, "HOW_TO_CHAT.md");
        AgentsPath = Path.Combine(Root, "AGENTS.md");
    }

    public static ChatWorkspace Initialize(string root)
    {
        var workspace = new ChatWorkspace(root);
        workspace.EnsureWithinRoot();
        Directory.CreateDirectory(workspace.ChatDir);
        Directory.CreateDirectory(workspace.AssetsDir);
        Directory.CreateDirectory(workspace.GoalsDir);
        Directory.CreateDirectory(workspace.GoalStatusDir);
        Directory.CreateDirectory(workspace.MessagesDir);
        Directory.CreateDirectory(workspace.RolesDir);

        var validRoles = workspace.GetRoleNames().ToArray();
        if (validRoles.Length == 0)
        {
            workspace.CreateDefaultRole("implementer");
            workspace.CreateDefaultRole("reviewer");
        }

        foreach (var role in workspace.GetRoleNames())
        {
            workspace.EnsureRoleFiles(role);
        }

        workspace.EnsureGitIgnore();
        workspace.EnsureInstructionFiles();
        workspace.EnsureStateFile();
        HtmlViews.WriteUiShell(workspace);
        HtmlViews.RegenerateChat(workspace);
        return workspace;
    }

    public static ChatWorkspace OpenExisting(string root)
    {
        var workspace = new ChatWorkspace(root);
        workspace.EnsureWithinRoot();
        if (!Directory.Exists(workspace.ChatDir))
        {
            throw CliException.Validation("not_initialized", "simpleagentchat is not initialized. Run 'dotnet simpleagentchat.cs init' first.");
        }

        return workspace;
    }

    public string RoleDirectory(string role)
    {
        if (!NameRules.IsValidRoleName(role, allowReserved: false))
        {
            throw CliException.Validation("invalid_role", "Role name is not safe.");
        }

        return SafeCombine(RolesDir, role);
    }

    public string GoalPath(string name)
    {
        if (!NameRules.IsValidGoalOrAssetName(name))
        {
            throw CliException.Validation("invalid_goal", "Goal file name is not safe.");
        }

        return SafeCombine(GoalsDir, name);
    }

    public string GoalStatusPath(string name)
    {
        if (!NameRules.IsValidGoalOrAssetName(name))
        {
            throw CliException.Validation("invalid_goal", "Goal file name is not safe.");
        }

        return SafeCombine(GoalStatusDir, name + ".status.json");
    }

    public string AssetPath(string name)
    {
        if (!NameRules.IsValidGoalOrAssetName(name))
        {
            throw CliException.Validation("invalid_asset", "Asset file name is not safe.");
        }

        return SafeCombine(AssetsDir, name);
    }

    public IReadOnlyList<string> GetRoleNames()
    {
        if (!Directory.Exists(RolesDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(RolesDir)
            .Select(Path.GetFileName)
            .Where(name => NameRules.IsValidRoleName(name, allowReserved: false))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> GetGoalNames()
    {
        if (!Directory.Exists(GoalsDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(GoalsDir)
            .Select(Path.GetFileName)
            .Where(NameRules.IsValidGoalOrAssetName)
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> GetAssetNames()
    {
        if (!Directory.Exists(AssetsDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(AssetsDir)
            .Select(Path.GetFileName)
            .Where(NameRules.IsValidGoalOrAssetName)
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public string SafeCombine(string baseDir, string childName)
    {
        var baseFull = Path.GetFullPath(baseDir);
        var combined = Path.GetFullPath(Path.Combine(baseFull, childName));
        if (!PathSafety.IsUnderDirectory(combined, baseFull))
        {
            throw CliException.Validation("unsafe_path", "Resolved path escapes the allowed simpleagentchat directory.");
        }

        return combined;
    }

    private void EnsureWithinRoot()
    {
        foreach (var path in new[] { ChatDir, GitIgnorePath, HowToChatPath, AgentsPath })
        {
            if (!PathSafety.IsUnderDirectory(Path.GetFullPath(path), Root, allowEqual: false) &&
                !string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(Root, Path.GetFileName(path))), StringComparison.OrdinalIgnoreCase))
            {
                throw CliException.Validation("unsafe_path", "A managed path resolved outside the repository root.");
            }
        }
    }

    private void CreateDefaultRole(string role)
    {
        Directory.CreateDirectory(RoleDirectory(role));
        EnsureRoleFiles(role);
    }

    public void EnsureRoleFiles(string role)
    {
        var dir = RoleDirectory(role);
        Directory.CreateDirectory(dir);
        var instructionsPath = Path.Combine(dir, "instructions.md");
        var memoryPath = Path.Combine(dir, "role_memory.md");
        if (!File.Exists(instructionsPath))
        {
            Atomic.WriteText(instructionsPath, DefaultRoleInstructions(role));
        }

        if (!File.Exists(memoryPath))
        {
            Atomic.WriteText(memoryPath, "# Role Memory\n\nNo durable notes yet.\n");
        }
    }

    private void EnsureGitIgnore()
    {
        var content = File.Exists(GitIgnorePath) ? File.ReadAllText(GitIgnorePath, Encoding.UTF8) : "";
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hasEntry = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(line => line.Trim())
            .Any(line => string.Equals(line, ".simpleagentchat", StringComparison.Ordinal) ||
                         string.Equals(line, ".simpleagentchat/", StringComparison.Ordinal));
        if (hasEntry)
        {
            return;
        }

        var builder = new StringBuilder(content);
        if (builder.Length > 0 && !content.EndsWith("\n", StringComparison.Ordinal) && !content.EndsWith("\r", StringComparison.Ordinal))
        {
            builder.Append(newline);
        }

        builder.Append(".simpleagentchat/").Append(newline);
        Atomic.WriteText(GitIgnorePath, builder.ToString());
    }

    private void EnsureInstructionFiles()
    {
        var howBlock = MarkdownBlocks.HowToChatBlock();
        MarkedBlock.Upsert(HowToChatPath, howBlock);
        var agentsBlock = "If you are asked to join a simpleagentchat chat, read HOW_TO_CHAT.md first and follow it.\n";
        MarkedBlock.Upsert(AgentsPath, agentsBlock);
    }

    private void EnsureStateFile()
    {
        if (File.Exists(StatePath))
        {
            return;
        }

        var state = new
        {
            schemaVersion = 1,
            initializedAtUtc = Time.UtcNowRoundTrip()
        };
        Atomic.WriteText(StatePath, Json.Text(state) + "\n");
    }

    private static string DefaultRoleInstructions(string role) => role switch
    {
        "implementer" => "# Implementer Role\n\nImplement the goal cleanly and efficiently. Prefer pragmatic DRY and YAGNI. Fetch before meaningful work, read current goals and role memory, and wait for a human `Start` unless one already appears in fetched history.\n",
        "reviewer" => "# Reviewer Role\n\nReview work for correctness, risks, regressions, missing tests, and clarity. Fetch before meaningful review, read current goals and role memory, and mark goals done only after checking the implementation from this role's responsibility.\n",
        _ => $"# {role} Role\n\nFollow HOW_TO_CHAT.md. Read this file, role_memory.md, and current goals before participating.\n"
    };
}

internal static class PathSafety
{
    public static bool IsUnderDirectory(string path, string directory, bool allowEqual = true)
    {
        var pathFull = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirFull = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (allowEqual && string.Equals(pathFull, dirFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return pathFull.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               pathFull.StartsWith(dirFull + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class MarkedBlock
{
    private const string Start = "<!-- simpleagentchat:start -->";
    private const string End = "<!-- simpleagentchat:end -->";

    public static void Upsert(string path, string blockContent)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalizedBlock = blockContent.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
        var block = Start + newline + normalizedBlock.TrimEnd('\r', '\n') + newline + End;

        var startIndex = existing.IndexOf(Start, StringComparison.Ordinal);
        var endIndex = existing.IndexOf(End, StringComparison.Ordinal);
        string updated;
        if (startIndex >= 0 && endIndex > startIndex)
        {
            updated = existing[..startIndex] + block + existing[(endIndex + End.Length)..];
        }
        else if (string.IsNullOrEmpty(existing))
        {
            updated = block + newline;
        }
        else
        {
            var separator = existing.EndsWith("\n", StringComparison.Ordinal) || existing.EndsWith("\r", StringComparison.Ordinal)
                ? newline
                : newline + newline;
            updated = existing + separator + block + newline;
        }

        Atomic.WriteText(path, updated);
    }
}

internal static class MarkdownBlocks
{
    public static string HowToChatBlock() => """
# simpleagentchat

The active chat lives in `.simpleagentchat/`.

Before joining as an agent:

- Fetch all prior messages with `dotnet simpleagentchat.cs fetch --json`.
- Initial fetches with no cursor omit historical `system` messages by default; current role files, goal files, and role memory are authoritative.
- Preserve the returned `nextCursor`, even when messages were filtered out.
- Read all files in `.simpleagentchat/goals/`.
- Read `.simpleagentchat/roles/<role>/instructions.md`.
- Read `.simpleagentchat/roles/<role>/role_memory.md`.
- Review prior non-system messages from your role and continue from where that role left off.
- Do not begin implementation work until a human message exactly says `Start`, unless a previous fetched human `Start` already exists.

During work:

- Fetch from your latest fetched cursor before each meaningful step.
- Do not advance your fetch cursor from your own `say` result. Advance it only from `fetch`.
- Obey critical messages that start with `!` immediately.
- Obey newly fetched `system` messages immediately.
- Re-read role instructions, role memory, and goals when instructed.
- Stop if your role directory no longer exists.
- Put assets in `.simpleagentchat/assets/` before referencing them.

Goal completion:

- A goal is complete only when every current valid role has publicly marked it done.
- Use `dotnet simpleagentchat.cs goal status <goal_file_name>` before claiming a goal is complete.
- Use `dotnet simpleagentchat.cs goal done <role> <goal_file_name>` only after checking the goal from your role's responsibility.
- Use `dotnet simpleagentchat.cs goal undone <role> <goal_file_name>` when new evidence invalidates previous agreement.
- Use `dotnet simpleagentchat.cs goal recheck <goal_file_name> <reason>` when important changes require every role to re-check and re-approve.

Roles:

- Do not use the `goal` role unless you are the tool.
- Do not use the `human` role unless you are the human.
- Do not use the `system` role unless you are the tool.
- All chat is public.
""";
}

internal sealed class GoalStatusStore
{
    private readonly ChatWorkspace _workspace;

    public GoalStatusStore(ChatWorkspace workspace)
    {
        _workspace = workspace;
    }

    public GoalStatusReport GetStatus(string goalName)
    {
        ValidateGoalExists(goalName);
        var document = ReadDocument(goalName);
        var roles = _workspace.GetRoleNames();
        var roleStatuses = new List<GoalRoleStatus>();

        foreach (var role in roles)
        {
            if (document.Roles.TryGetValue(role, out var entry) &&
                string.Equals(entry.Status, "done", StringComparison.Ordinal))
            {
                roleStatuses.Add(new GoalRoleStatus(role, "done", entry.UpdatedAtUtc, entry.MessageId));
            }
            else if (document.Roles.TryGetValue(role, out var explicitEntry) &&
                     string.Equals(explicitEntry.Status, "undone", StringComparison.Ordinal))
            {
                roleStatuses.Add(new GoalRoleStatus(role, "undone", explicitEntry.UpdatedAtUtc, explicitEntry.MessageId));
            }
            else
            {
                roleStatuses.Add(new GoalRoleStatus(role, "undone", null, null));
            }
        }

        var complete = roleStatuses.Count > 0 && roleStatuses.All(status => status.Status == "done");
        return new GoalStatusReport(goalName, complete, roleStatuses);
    }

    public async Task<Message> MarkRoleStatusAsync(string goalName, string role, string status, int waitMs)
    {
        ValidateGoalExists(goalName);
        ValidateRoleExists(role);
        if (status is not "done" and not "undone")
        {
            throw CliException.Validation("invalid_status", "Goal status must be done or undone.");
        }

        var store = new MessageStore(_workspace);
        var statusPath = RootRelative(_workspace.GoalStatusPath(goalName));
        var markdown = status == "done"
            ? $"Role `{role}` marked goal `{goalName}` done."
            : $"Role `{role}` marked goal `{goalName}` undone.";
        var kind = status == "done" ? "goals.done" : "goals.undone";
        var message = store.NewMessage(role, kind, markdown, new[] { statusPath });

        await Retry.WithinAsync(waitMs, async () =>
        {
            var document = NormalizeDocument(goalName, ReadDocument(goalName));
            document.Roles[role] = new RoleStatusEntry
            {
                Status = status,
                UpdatedAtUtc = message.TimestampUtc,
                MessageId = message.Id
            };
            SyncCurrentRoles(document, goalName);
            WriteDocument(goalName, document);
            await store.WriteMessageFileNoRetryAsync(message);
            HtmlViews.RegenerateChat(_workspace);
        });

        return message;
    }

    public async Task<RecheckMessages> RecheckAsync(string goalName, string reason, int waitMs)
    {
        ValidateGoalExists(goalName);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw CliException.Validation("empty_reason", "goal recheck reason must be non-empty.");
        }

        var store = new MessageStore(_workspace);
        var statusPath = RootRelative(_workspace.GoalStatusPath(goalName));
        var system = store.NewMessage(
            "system",
            "goals.recheck",
            $"Goals changed. All current roles must re-check and re-approve `{goalName}`.",
            new[] { statusPath, RootRelative(_workspace.GoalPath(goalName)) });
        var goalMessage = store.NewMessage("goal", "chat.message", reason, Array.Empty<string>());

        await Retry.WithinAsync(waitMs, async () =>
        {
            var document = NormalizeDocument(goalName, new GoalStatusDocument());
            foreach (var role in _workspace.GetRoleNames())
            {
                document.Roles[role] = new RoleStatusEntry
                {
                    Status = "undone",
                    UpdatedAtUtc = system.TimestampUtc,
                    MessageId = system.Id
                };
            }

            document.Complete = false;
            WriteDocument(goalName, document);
            await store.WriteMessageFileNoRetryAsync(system);
            await store.WriteMessageFileNoRetryAsync(goalMessage);
            HtmlViews.RegenerateChat(_workspace);
        });

        return new RecheckMessages(system, goalMessage);
    }

    public void ResetGoalForCurrentRoles(string goalName, string? updatedAtUtc = null, string? messageId = null)
    {
        ValidateGoalExists(goalName);
        var document = NormalizeDocument(goalName, new GoalStatusDocument());
        foreach (var role in _workspace.GetRoleNames())
        {
            document.Roles[role] = new RoleStatusEntry
            {
                Status = "undone",
                UpdatedAtUtc = updatedAtUtc,
                MessageId = messageId
            };
        }

        document.Complete = false;
        WriteDocument(goalName, document);
    }

    private void ValidateGoalExists(string goalName)
    {
        if (!NameRules.IsValidGoalOrAssetName(goalName))
        {
            throw CliException.Validation("invalid_goal", "Goal file name is not safe.");
        }

        if (!File.Exists(_workspace.GoalPath(goalName)))
        {
            throw CliException.Validation("missing_goal", $"Goal '{goalName}' does not exist.");
        }
    }

    private void ValidateRoleExists(string role)
    {
        if (!NameRules.IsValidRoleName(role, allowReserved: false))
        {
            throw CliException.Validation("invalid_role", "Role is not a valid current agent role name.");
        }

        if (!Directory.Exists(_workspace.RoleDirectory(role)))
        {
            throw CliException.Validation("missing_role", $"Role '{role}' does not exist.");
        }
    }

    private GoalStatusDocument ReadDocument(string goalName)
    {
        var path = _workspace.GoalStatusPath(goalName);
        if (!File.Exists(path))
        {
            return NormalizeDocument(goalName, new GoalStatusDocument());
        }

        try
        {
            var document = JsonSerializer.Deserialize<GoalStatusDocument>(File.ReadAllText(path, Encoding.UTF8), Json.Options) ?? new GoalStatusDocument();
            return NormalizeDocument(goalName, document);
        }
        catch (JsonException)
        {
            return NormalizeDocument(goalName, new GoalStatusDocument());
        }
    }

    private GoalStatusDocument NormalizeDocument(string goalName, GoalStatusDocument document)
    {
        document.Goal = goalName;
        document.Roles ??= new Dictionary<string, RoleStatusEntry>(StringComparer.Ordinal);
        document.Roles = document.Roles
            .Where(pair => NameRules.IsValidRoleName(pair.Key, allowReserved: false))
            .ToDictionary(pair => pair.Key, pair => NormalizeEntry(pair.Value), StringComparer.Ordinal);
        document.Complete = false;
        return document;
    }

    private void SyncCurrentRoles(GoalStatusDocument document, string goalName)
    {
        var currentRoles = _workspace.GetRoleNames().ToHashSet(StringComparer.Ordinal);
        foreach (var role in currentRoles)
        {
            if (!document.Roles.ContainsKey(role))
            {
                document.Roles[role] = new RoleStatusEntry { Status = "undone" };
            }
        }

        foreach (var role in document.Roles.Keys.Where(role => !currentRoles.Contains(role)).ToArray())
        {
            document.Roles.Remove(role);
        }

        document.Goal = goalName;
        document.Complete = currentRoles.Count > 0 && currentRoles.All(role => document.Roles.TryGetValue(role, out var entry) && entry.Status == "done");
    }

    private void WriteDocument(string goalName, GoalStatusDocument document)
    {
        SyncCurrentRoles(document, goalName);
        Atomic.WriteText(_workspace.GoalStatusPath(goalName), Json.Text(document) + "\n");
    }

    private static RoleStatusEntry NormalizeEntry(RoleStatusEntry? entry)
    {
        if (entry is null)
        {
            return new RoleStatusEntry { Status = "undone" };
        }

        if (entry.Status is not "done" and not "undone")
        {
            entry.Status = "undone";
            entry.UpdatedAtUtc = null;
            entry.MessageId = null;
        }

        return entry;
    }

    private string RootRelative(string fullPath)
    {
        return Path.GetRelativePath(_workspace.Root, fullPath).Replace('\\', '/');
    }
}

internal sealed class GoalStatusDocument
{
    public string Goal { get; set; } = "";
    public bool Complete { get; set; }
    public Dictionary<string, RoleStatusEntry> Roles { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class RoleStatusEntry
{
    public string Status { get; set; } = "undone";
    public string? UpdatedAtUtc { get; set; }
    public string? MessageId { get; set; }
}

internal sealed record GoalStatusReport(string Goal, bool Complete, IReadOnlyList<GoalRoleStatus> Roles);
internal sealed record GoalRoleStatus(string Role, string Status, string? UpdatedAtUtc, string? MessageId);
internal sealed record RecheckMessages(Message SystemMessage, Message GoalMessage);

internal sealed class LocalServer
{
    private const int DefaultStartPort = 8765;
    private const int DefaultEndPort = 8799;
    private const long MaxAssetBytes = 25L * 1024L * 1024L;
    private readonly ChatWorkspace _workspace;
    private readonly int? _requestedPort;
    private readonly bool _noOpen;
    private int _port;

    public LocalServer(ChatWorkspace workspace, int? requestedPort, bool noOpen)
    {
        _workspace = workspace;
        _requestedPort = requestedPort;
        _noOpen = noOpen;
    }

    public async Task<int> RunAsync()
    {
        using var listener = StartListener();
        var url = $"http://127.0.0.1:{_port}/";
        if (_noOpen)
        {
            Console.Out.WriteLine(url);
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                Console.Out.WriteLine(url);
            }
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
            listener.Stop();
        };

        while (!cancellation.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException) when (cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleContextAsync(context), cancellation.Token);
        }

        return 0;
    }

    private HttpListener StartListener()
    {
        if (_requestedPort.HasValue)
        {
            return TryStart(_requestedPort.Value) ??
                   throw CliException.ServerStartup($"Port {_requestedPort.Value} is unavailable.");
        }

        for (var port = DefaultStartPort; port <= DefaultEndPort; port++)
        {
            var listener = TryStart(port);
            if (listener is not null)
            {
                return listener;
            }
        }

        throw CliException.ServerStartup($"No loopback port from {DefaultStartPort} through {DefaultEndPort} is available.");
    }

    private HttpListener? TryStart(int port)
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _port = port;
            return listener;
        }
        catch
        {
            return null;
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        try
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await RouteAsync(context);
        }
        catch (ApiException ex)
        {
            await WriteJsonAsync(context, ex.StatusCode, new { error = new { code = ex.Code, message = ex.Message } });
        }
        catch (CliException ex)
        {
            await WriteJsonAsync(context, 400, new { error = new { code = ex.Code, message = ex.Message } });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new { error = new { code = "server_error", message = ex.Message } });
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task RouteAsync(HttpListenerContext context)
    {
        var method = context.Request.HttpMethod.ToUpperInvariant();
        var segments = GetSegments(context.Request);

        if (segments.Length == 0 && method == "GET")
        {
            await SendFileAsync(context, _workspace.UiHtmlPath, "text/html; charset=utf-8", attachment: false);
            return;
        }

        if (segments is ["chat.html"] && method == "GET")
        {
            HtmlViews.RegenerateChat(_workspace);
            await SendFileAsync(context, _workspace.ChatHtmlPath, "text/html; charset=utf-8", attachment: false);
            return;
        }

        if (segments.Length >= 1 && segments[0] == "assets")
        {
            await RouteAssetFileAsync(context, method, segments);
            return;
        }

        if (segments.Length >= 1 && segments[0] == "api")
        {
            await RouteApiAsync(context, method, segments);
            return;
        }

        throw new ApiException(404, "not_found", "Endpoint not found.");
    }

    private async Task RouteApiAsync(HttpListenerContext context, string method, string[] segments)
    {
        if (segments.Length == 2 && segments[1] == "messages")
        {
            if (method == "GET")
            {
                await GetMessagesAsync(context);
                return;
            }

            if (method == "POST")
            {
                await PostMessageAsync(context);
                return;
            }
        }

        if (segments.Length >= 2 && segments[1] == "roles")
        {
            await RouteRolesAsync(context, method, segments);
            return;
        }

        if (segments.Length >= 2 && segments[1] == "goals")
        {
            await RouteGoalsAsync(context, method, segments);
            return;
        }

        if (segments.Length >= 2 && segments[1] == "assets")
        {
            await RouteAssetsApiAsync(context, method, segments);
            return;
        }

        throw new ApiException(404, "not_found", "API endpoint not found.");
    }

    private async Task GetMessagesAsync(HttpListenerContext context)
    {
        var query = context.Request.QueryString;
        var cursor = EmptyToNull(query["cursor"]);
        if (cursor is not null && !NameRules.IsValidMessageId(cursor))
        {
            throw CliException.Validation("invalid_cursor", "Cursor is not a valid simpleagentchat message id.");
        }

        var waitMs = string.IsNullOrWhiteSpace(query["waitMs"]) ? 0 : CliParsing.ParseWaitMs(query["waitMs"]!);
        var includeSystem = string.IsNullOrWhiteSpace(query["includeSystem"])
            ? true
            : ParseBool(query["includeSystem"]!, "includeSystem");
        var response = await new MessageStore(_workspace).FetchAsync(cursor, includeSystem, waitMs);
        await WriteJsonAsync(context, 200, response);
    }

    private async Task PostMessageAsync(HttpListenerContext context)
    {
        using var document = await ReadJsonDocumentAsync(context.Request);
        var markdown = GetString(document, "markdown") ?? "";
        var critical = GetBool(document, "critical") ?? false;
        if (critical && !markdown.TrimStart().StartsWith("!", StringComparison.Ordinal))
        {
            markdown = "! " + markdown.TrimStart();
        }

        var message = await new MessageStore(_workspace).AppendAsync("human", "chat.message", markdown, Array.Empty<string>(), 30000);
        await WriteJsonAsync(context, 200, new { message, nextCursor = message.Id });
    }

    private async Task RouteRolesAsync(HttpListenerContext context, string method, string[] segments)
    {
        if (segments.Length == 2 && method == "GET")
        {
            var roles = _workspace.GetRoleNames().Select(role =>
            {
                var dir = _workspace.RoleDirectory(role);
                var instructions = new FileInfo(Path.Combine(dir, "instructions.md"));
                var memory = new FileInfo(Path.Combine(dir, "role_memory.md"));
                return new
                {
                    role,
                    instructionsLength = instructions.Exists ? instructions.Length : 0,
                    memoryLength = memory.Exists ? memory.Length : 0,
                    updatedAtUtc = LatestWriteUtc(instructions, memory)
                };
            }).ToArray();
            await WriteJsonAsync(context, 200, new { roles });
            return;
        }

        if (segments.Length == 3 && method == "GET")
        {
            await WriteJsonAsync(context, 200, ReadRole(segments[2]));
            return;
        }

        if (segments.Length == 3 && method == "DELETE")
        {
            await DeleteRoleAsync(context, segments[2]);
            return;
        }

        if (segments.Length == 4 && method == "PUT" && segments[3] == "instructions")
        {
            await PutRoleInstructionsAsync(context, segments[2]);
            return;
        }

        if (segments.Length == 4 && method == "PUT" && segments[3] == "memory")
        {
            await PutRoleMemoryAsync(context, segments[2]);
            return;
        }

        throw new ApiException(404, "not_found", "Role endpoint not found.");
    }

    private object ReadRole(string role)
    {
        if (!NameRules.IsValidRoleName(role, allowReserved: false))
        {
            throw CliException.Validation("invalid_role", "Role name is not safe.");
        }

        var dir = _workspace.RoleDirectory(role);
        if (!Directory.Exists(dir))
        {
            throw new ApiException(404, "missing_role", $"Role '{role}' does not exist.");
        }

        return new
        {
            role,
            instructions = File.ReadAllText(Path.Combine(dir, "instructions.md"), Encoding.UTF8),
            memory = File.ReadAllText(Path.Combine(dir, "role_memory.md"), Encoding.UTF8)
        };
    }

    private async Task PutRoleInstructionsAsync(HttpListenerContext context, string role)
    {
        if (!NameRules.IsValidRoleName(role, allowReserved: false))
        {
            throw CliException.Validation("invalid_role", "Role name is not safe.");
        }

        using var document = await ReadJsonDocumentAsync(context.Request);
        var markdown = GetString(document, "markdown") ?? "";
        _workspace.EnsureRoleFiles(role);
        var path = Path.Combine(_workspace.RoleDirectory(role), "instructions.md");
        Atomic.WriteText(path, markdown);
        var changedPath = RootRelative(path);
        var message = await new MessageStore(_workspace).AppendAsync(
            "system",
            "roles.changed",
            "Roles changed. Agents must re-read `.simpleagentchat/roles` before continuing.",
            new[] { changedPath },
            30000);
        await WriteJsonAsync(context, 200, new { role, message });
    }

    private async Task PutRoleMemoryAsync(HttpListenerContext context, string role)
    {
        if (!NameRules.IsValidRoleName(role, allowReserved: false))
        {
            throw CliException.Validation("invalid_role", "Role name is not safe.");
        }

        using var document = await ReadJsonDocumentAsync(context.Request);
        var markdown = GetString(document, "markdown") ?? "";
        _workspace.EnsureRoleFiles(role);
        var path = Path.Combine(_workspace.RoleDirectory(role), "role_memory.md");
        Atomic.WriteText(path, markdown);
        var changedPath = RootRelative(path);
        var message = await new MessageStore(_workspace).AppendAsync(
            "system",
            "roles.memory.changed",
            "Role memory changed. Agents using that role must re-read role memory before continuing.",
            new[] { changedPath },
            30000);
        await WriteJsonAsync(context, 200, new { role, message });
    }

    private async Task DeleteRoleAsync(HttpListenerContext context, string role)
    {
        if (!NameRules.IsValidRoleName(role, allowReserved: false))
        {
            throw CliException.Validation("invalid_role", "Role name is not safe.");
        }

        var dir = _workspace.RoleDirectory(role);
        if (!Directory.Exists(dir))
        {
            throw new ApiException(404, "missing_role", $"Role '{role}' does not exist.");
        }

        Directory.Delete(dir, recursive: true);
        var message = await new MessageStore(_workspace).AppendAsync(
            "system",
            "roles.deleted",
            $"! Role \"{role}\" was deleted. Any agent using that role must stop immediately.",
            new[] { RootRelative(dir) },
            30000);
        await WriteJsonAsync(context, 200, new { role, deleted = true, message });
    }

    private async Task RouteGoalsAsync(HttpListenerContext context, string method, string[] segments)
    {
        if (segments.Length == 2 && method == "GET")
        {
            var statusStore = new GoalStatusStore(_workspace);
            var goals = _workspace.GetGoalNames().Select(name =>
            {
                var file = new FileInfo(_workspace.GoalPath(name));
                var status = statusStore.GetStatus(name);
                return new
                {
                    name,
                    length = file.Exists ? file.Length : 0,
                    updatedAtUtc = file.Exists ? Time.RoundTrip(file.LastWriteTimeUtc) : null,
                    complete = status.Complete
                };
            }).ToArray();
            await WriteJsonAsync(context, 200, new { goals });
            return;
        }

        if (segments.Length == 3 && method == "GET")
        {
            await WriteJsonAsync(context, 200, ReadGoal(segments[2]));
            return;
        }

        if (segments.Length == 3 && method == "PUT")
        {
            await PutGoalAsync(context, segments[2]);
            return;
        }

        if (segments.Length == 3 && method == "DELETE")
        {
            await DeleteGoalAsync(context, segments[2]);
            return;
        }

        throw new ApiException(404, "not_found", "Goal endpoint not found.");
    }

    private object ReadGoal(string name)
    {
        if (!NameRules.IsValidGoalOrAssetName(name))
        {
            throw CliException.Validation("invalid_goal", "Goal file name is not safe.");
        }

        var path = _workspace.GoalPath(name);
        if (!File.Exists(path))
        {
            throw new ApiException(404, "missing_goal", $"Goal '{name}' does not exist.");
        }

        return new { name, content = File.ReadAllText(path, Encoding.UTF8) };
    }

    private async Task PutGoalAsync(HttpListenerContext context, string name)
    {
        if (!NameRules.IsValidGoalOrAssetName(name))
        {
            throw CliException.Validation("invalid_goal", "Goal file name is not safe.");
        }

        using var document = await ReadJsonDocumentAsync(context.Request);
        var content = GetString(document, "content") ?? "";
        var goalPath = _workspace.GoalPath(name);
        var statusPath = _workspace.GoalStatusPath(name);
        var store = new MessageStore(_workspace);
        var message = store.NewMessage(
            "system",
            "goals.changed",
            "Goals changed. Agents must re-read `.simpleagentchat/goals` before continuing.",
            new[] { RootRelative(goalPath), RootRelative(statusPath) });

        await Retry.WithinAsync(30000, async () =>
        {
            Atomic.WriteText(goalPath, content);
            new GoalStatusStore(_workspace).ResetGoalForCurrentRoles(name, message.TimestampUtc, message.Id);
            await store.WriteMessageFileNoRetryAsync(message);
            HtmlViews.RegenerateChat(_workspace);
        });

        await WriteJsonAsync(context, 200, new { name, message });
    }

    private async Task DeleteGoalAsync(HttpListenerContext context, string name)
    {
        if (!NameRules.IsValidGoalOrAssetName(name))
        {
            throw CliException.Validation("invalid_goal", "Goal file name is not safe.");
        }

        var goalPath = _workspace.GoalPath(name);
        if (!File.Exists(goalPath))
        {
            throw new ApiException(404, "missing_goal", $"Goal '{name}' does not exist.");
        }

        var statusPath = _workspace.GoalStatusPath(name);
        File.Delete(goalPath);
        if (File.Exists(statusPath))
        {
            File.Delete(statusPath);
        }

        var message = await new MessageStore(_workspace).AppendAsync(
            "system",
            "goals.changed",
            "Goals changed. Agents must re-read `.simpleagentchat/goals` before continuing.",
            new[] { RootRelative(goalPath), RootRelative(statusPath) },
            30000);
        await WriteJsonAsync(context, 200, new { name, deleted = true, message });
    }

    private async Task RouteAssetsApiAsync(HttpListenerContext context, string method, string[] segments)
    {
        if (segments.Length == 2 && method == "GET")
        {
            var assets = _workspace.GetAssetNames().Select(name =>
            {
                var file = new FileInfo(_workspace.AssetPath(name));
                return new
                {
                    name,
                    length = file.Exists ? file.Length : 0,
                    updatedAtUtc = file.Exists ? Time.RoundTrip(file.LastWriteTimeUtc) : null
                };
            }).ToArray();
            await WriteJsonAsync(context, 200, new { assets });
            return;
        }

        if (segments.Length == 3 && method == "PUT")
        {
            await PutAssetAsync(context, segments[2]);
            return;
        }

        throw new ApiException(404, "not_found", "Asset endpoint not found.");
    }

    private async Task PutAssetAsync(HttpListenerContext context, string name)
    {
        if (!NameRules.IsValidGoalOrAssetName(name))
        {
            throw CliException.Validation("invalid_asset", "Asset file name is not safe.");
        }

        if (context.Request.ContentLength64 > MaxAssetBytes)
        {
            throw new ApiException(413, "asset_too_large", "Asset upload exceeds the 25 MiB limit.");
        }

        var path = _workspace.AssetPath(name);
        var tempPath = Path.Combine(_workspace.AssetsDir, "." + name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        long total = 0;
        Directory.CreateDirectory(_workspace.AssetsDir);
        try
        {
            await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (true)
                {
                    var read = await context.Request.InputStream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaxAssetBytes)
                    {
                        throw new ApiException(413, "asset_too_large", "Asset upload exceeds the 25 MiB limit.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }

        File.Move(tempPath, path, overwrite: true);
        await WriteJsonAsync(context, 200, new { name, length = total });
    }

    private async Task RouteAssetFileAsync(HttpListenerContext context, string method, string[] segments)
    {
        if (segments.Length != 2 || method != "GET")
        {
            throw new ApiException(404, "not_found", "Asset endpoint not found.");
        }

        var name = segments[1];
        if (!NameRules.IsValidGoalOrAssetName(name))
        {
            throw CliException.Validation("invalid_asset", "Asset file name is not safe.");
        }

        var path = _workspace.AssetPath(name);
        if (!File.Exists(path))
        {
            throw new ApiException(404, "missing_asset", $"Asset '{name}' does not exist.");
        }

        var (contentType, attachment) = AssetContentType(name);
        await SendFileAsync(context, path, contentType, attachment);
    }

    private static string[] GetSegments(HttpListenerRequest request)
    {
        var absolutePath = request.Url?.AbsolutePath ?? "/";
        if (absolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            absolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            throw CliException.Validation("unsafe_path", "Encoded path separators are not allowed.");
        }

        return absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => Uri.UnescapeDataString(segment))
            .ToArray();
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException ex)
        {
            throw CliException.Validation("invalid_json", ex.Message);
        }
    }

    private static string? GetString(JsonDocument document, string propertyName)
    {
        return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? GetBool(JsonDocument document, string propertyName)
    {
        return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static bool ParseBool(string value, string name)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw CliException.Validation("invalid_bool", $"{name} must be true or false.");
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task WriteJsonAsync(HttpListenerContext context, int statusCode, object body)
    {
        await WriteBytesAsync(context, statusCode, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json.Text(body) + "\n"));
    }

    private static async Task SendFileAsync(HttpListenerContext context, string path, string contentType, bool attachment)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (attachment)
        {
            context.Response.Headers["Content-Disposition"] = "attachment; filename=\"" + Path.GetFileName(path).Replace("\"", "", StringComparison.Ordinal) + "\"";
        }

        var info = new FileInfo(path);
        context.Response.ContentLength64 = info.Length;
        await using var input = File.OpenRead(path);
        await input.CopyToAsync(context.Response.OutputStream);
    }

    private static async Task WriteBytesAsync(HttpListenerContext context, int statusCode, string contentType, byte[] bytes)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private static (string ContentType, bool Attachment) AssetContentType(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".txt" => ("text/plain; charset=utf-8", false),
            ".md" => ("text/markdown; charset=utf-8", false),
            ".png" => ("image/png", false),
            ".jpg" or ".jpeg" => ("image/jpeg", false),
            ".gif" => ("image/gif", false),
            ".webp" => ("image/webp", false),
            ".pdf" => ("application/pdf", false),
            ".html" or ".htm" or ".svg" or ".xml" or ".js" or ".mjs" or ".css" => ("application/octet-stream", true),
            _ => ("application/octet-stream", false)
        };
    }

    private static string? LatestWriteUtc(params FileInfo[] files)
    {
        var existing = files.Where(file => file.Exists).ToArray();
        if (existing.Length == 0)
        {
            return null;
        }

        return Time.RoundTrip(existing.Max(file => file.LastWriteTimeUtc));
    }

    private string RootRelative(string fullPath)
    {
        return Path.GetRelativePath(_workspace.Root, fullPath).Replace('\\', '/');
    }
}

internal sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }

    public ApiException(int statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }
}

internal sealed class MessageStore
{
    private readonly ChatWorkspace _workspace;
    private static readonly object ClockLock = new();
    private static DateTimeOffset _lastTimestamp = DateTimeOffset.MinValue;

    public MessageStore(ChatWorkspace workspace)
    {
        _workspace = workspace;
    }

    public async Task<Message> AppendAsync(string role, string kind, string markdown, IReadOnlyList<string> changedPaths, int waitMs)
    {
        var message = NewMessage(role, kind, markdown, changedPaths);
        await AppendPreparedBatchAsync(new[] { message }, waitMs);
        return message;
    }

    public Message NewMessage(string role, string kind, string markdown, IReadOnlyList<string> changedPaths)
    {
        if (!NameRules.IsValidMessageRole(role))
        {
            throw CliException.Validation("invalid_role", "Message role is not valid.");
        }

        return CreateMessage(role, kind, markdown, changedPaths);
    }

    public Task AppendPreparedBatchAsync(IReadOnlyList<Message> messages, int waitMs)
    {
        return Retry.WithinAsync(waitMs, async () =>
        {
            foreach (var message in messages)
            {
                await WriteMessageFileNoRetryAsync(message);
            }

            HtmlViews.RegenerateChat(_workspace);
        });
    }

    internal async Task WriteMessageFileNoRetryAsync(Message message)
    {
        Directory.CreateDirectory(_workspace.MessagesDir);
        var finalPath = Path.Combine(_workspace.MessagesDir, message.Id + ".json");
        var tempPath = Path.Combine(_workspace.MessagesDir, "." + message.Id + "." + Guid.NewGuid().ToString("N") + ".tmp");
        await Atomic.WriteTextAsync(tempPath, Json.Text(message) + "\n");
        try
        {
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }

    public async Task<FetchResponse> FetchAsync(string? cursor, bool includeSystem, int waitMs)
    {
        if (cursor is not null && !NameRules.IsValidMessageId(cursor))
        {
            throw CliException.Validation("invalid_cursor", "Cursor is not a valid simpleagentchat message id.");
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        while (true)
        {
            var allMessages = ReadAllMessages();
            var newest = allMessages.Count == 0 ? null : allMessages[^1].Id;
            var newer = cursor is null
                ? allMessages
                : allMessages.Where(m => string.CompareOrdinal(m.Id, cursor) > 0).ToList();
            var filtered = includeSystem ? newer : newer.Where(m => m.Role != "system").ToList();

            if (filtered.Count > 0 || (newer.Count > 0 && filtered.Count == 0) || waitMs == 0 || DateTime.UtcNow >= deadline)
            {
                var timedOut = filtered.Count == 0 && newer.Count == 0;
                return new FetchResponse(newest, timedOut, filtered);
            }

            await Task.Delay(Math.Min(200, Math.Max(10, waitMs)));
        }
    }

    public List<Message> ReadAllMessages()
    {
        if (!Directory.Exists(_workspace.MessagesDir))
        {
            return new List<Message>();
        }

        var messages = new List<Message>();
        foreach (var file in Directory.EnumerateFiles(_workspace.MessagesDir, "*.json").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!NameRules.IsValidMessageId(id))
            {
                continue;
            }

            try
            {
                var message = JsonSerializer.Deserialize<Message>(File.ReadAllText(file, Encoding.UTF8), Json.Options);
                if (message is not null && message.Id == id && NameRules.IsValidMessageRole(message.Role))
                {
                    messages.Add(message);
                }
            }
            catch (JsonException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
        }

        messages.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return messages;
    }

    private static Message CreateMessage(string role, string kind, string markdown, IReadOnlyList<string> changedPaths)
    {
        var timestamp = NextTimestamp();
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var id = timestamp.ToString("yyyyMMdd'T'HHmmss.fffffff'Z'", CultureInfo.InvariantCulture) + "-" + role + "-" + random;
        return new Message(id, Time.RoundTrip(timestamp), role, kind, markdown, changedPaths.ToArray());
    }

    private static DateTimeOffset NextTimestamp()
    {
        lock (ClockLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now <= _lastTimestamp)
            {
                now = _lastTimestamp.AddTicks(1);
            }

            _lastTimestamp = now;
            return now;
        }
    }
}

internal sealed record Message(
    string Id,
    string TimestampUtc,
    string Role,
    string Kind,
    string Markdown,
    string[] ChangedPaths);

internal sealed record FetchResponse(
    string? NextCursor,
    bool TimedOut,
    IReadOnlyList<Message> Messages);

internal static class PlainText
{
    public static void WriteFetch(FetchResponse response)
    {
        Console.Out.WriteLine($"nextCursor: {response.NextCursor ?? "null"}");
        Console.Out.WriteLine($"timedOut: {response.TimedOut.ToString().ToLowerInvariant()}");
        Console.Out.WriteLine($"messageCount: {response.Messages.Count}");
        foreach (var message in response.Messages)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"--- message {message.Id}");
            Console.Out.WriteLine($"timestampUtc: {message.TimestampUtc}");
            Console.Out.WriteLine($"role: {message.Role}");
            Console.Out.WriteLine($"kind: {message.Kind}");
            Console.Out.WriteLine("markdown:");
            Console.Out.WriteLine(message.Markdown);
            Console.Out.WriteLine("--- end");
        }
    }

    public static void WriteGoalStatus(GoalStatusReport report)
    {
        Console.Out.WriteLine($"goal: {report.Goal}");
        Console.Out.WriteLine($"complete: {report.Complete.ToString().ToLowerInvariant()}");
        Console.Out.WriteLine($"roleCount: {report.Roles.Count}");
        foreach (var role in report.Roles)
        {
            if (string.IsNullOrWhiteSpace(role.UpdatedAtUtc))
            {
                Console.Out.WriteLine($"{role.Role}: {role.Status}");
            }
            else
            {
                Console.Out.WriteLine($"{role.Role}: {role.Status} {role.UpdatedAtUtc}");
            }
        }
    }
}

internal static class HtmlViews
{
    public static void RegenerateChat(ChatWorkspace workspace)
    {
        var store = new MessageStore(workspace);
        var messages = store.ReadAllMessages();
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine("<meta http-equiv=\"refresh\" content=\"5\">");
        builder.AppendLine("<title>simpleagentchat transcript</title>");
        builder.AppendLine("<style>");
        builder.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#f7f7f4;color:#1d1d1f}main{max-width:980px;margin:0 auto;padding:24px}.message{border-top:1px solid #d8d8d2;padding:16px 0}.meta{font-size:12px;color:#5f6368;display:flex;gap:10px;flex-wrap:wrap}.role{font-weight:700;color:#1b4d3e}.system .role{color:#8a4600}.human .role{color:#164c86}.goal .role{color:#6f3d91}.markdown{line-height:1.55}.markdown pre{background:#202124;color:#f5f5f5;padding:12px;overflow:auto}.markdown code{background:#ededdf;padding:1px 4px;border-radius:3px}.markdown pre code{background:transparent;padding:0}.empty{color:#5f6368}</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body><main>");
        builder.AppendLine("<h1>simpleagentchat transcript</h1>");
        if (messages.Count == 0)
        {
            builder.AppendLine("<p class=\"empty\">No messages yet.</p>");
        }
        else
        {
            foreach (var message in messages)
            {
                var classes = "message " + HtmlAttribute(message.Role);
                builder.Append("<article class=\"").Append(classes).AppendLine("\">");
                builder.Append("<div class=\"meta\"><span class=\"role\">").Append(Html(message.Role)).Append("</span><span>").Append(Html(message.TimestampUtc)).Append("</span><span>").Append(Html(message.Kind)).Append("</span><span>").Append(Html(message.Id)).AppendLine("</span></div>");
                builder.Append("<div class=\"markdown\">").Append(Markdown.ToHtml(message.Markdown)).AppendLine("</div>");
                builder.AppendLine("</article>");
            }
        }

        builder.AppendLine("</main></body></html>");
        Atomic.WriteText(workspace.ChatHtmlPath, builder.ToString());
    }

    public static void WriteUiShell(ChatWorkspace workspace)
    {
        Atomic.WriteText(workspace.UiHtmlPath, UiShell.Html);
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
    private static string HtmlAttribute(string value) => Regex.Replace(value, "[^a-zA-Z0-9_-]", "-");
}

internal static class UiShell
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>simpleagentchat</title>
<style>
:root{color-scheme:light;--bg:#f7f7f4;--panel:#ffffff;--line:#d8d8d2;--text:#1d1d1f;--muted:#5f6368;--accent:#1b6f5c;--danger:#a83a32}
*{box-sizing:border-box}
[hidden]{display:none!important}
body{margin:0;background:var(--bg);color:var(--text);font-family:Segoe UI,Arial,sans-serif}
header{position:sticky;top:0;z-index:2;background:#fffffff2;border-bottom:1px solid var(--line);padding:12px 18px;display:flex;align-items:center;justify-content:space-between;gap:12px}
h1{font-size:18px;margin:0}
main{display:grid;grid-template-columns:minmax(0,1.4fr) minmax(320px,.9fr);gap:16px;padding:16px;max-width:1380px;margin:0 auto}
section{background:var(--panel);border:1px solid var(--line);border-radius:8px;min-width:0}
section h2{font-size:15px;margin:0;padding:12px 14px;border-bottom:1px solid var(--line)}
.body{padding:12px 14px}
textarea,input,select{width:100%;border:1px solid var(--line);border-radius:6px;padding:9px;font:inherit;background:#fff;color:var(--text)}
textarea{min-height:120px;resize:vertical}
button{border:1px solid var(--line);background:#fff;border-radius:6px;padding:8px 10px;font:inherit;cursor:pointer}
button.primary{background:var(--accent);border-color:var(--accent);color:#fff}
button.danger{color:#fff;background:var(--danger);border-color:var(--danger)}
.row{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.row>*{flex:1}
.row button,.row input[type=checkbox]{flex:0 0 auto}
.fields{display:grid;gap:10px}
.field{display:grid;gap:5px}
.field span{color:var(--muted);font-size:12px;font-weight:600;text-transform:uppercase;letter-spacing:.04em}
.formbar{display:grid;grid-template-columns:minmax(130px,1fr) minmax(130px,1fr) auto auto;gap:8px;align-items:end}
.formbar.assets{grid-template-columns:minmax(130px,1fr) minmax(170px,1fr) auto}
.tabs{display:flex;gap:6px;padding:8px;border-bottom:1px solid var(--line);flex-wrap:wrap}
.tabs button[aria-selected=true]{background:#173d36;color:#fff;border-color:#173d36}
iframe{width:100%;height:62vh;border:0;border-bottom:1px solid var(--line);background:#fff}
.list{display:grid;gap:6px;margin:8px 0}
.item{display:flex;justify-content:space-between;align-items:center;gap:8px;border:1px solid var(--line);border-radius:6px;padding:8px;background:#fff}
.muted{color:var(--muted);font-size:13px}
.stack{display:grid;gap:12px}
@media(max-width:900px){main{grid-template-columns:1fr}iframe{height:52vh}.formbar,.formbar.assets{grid-template-columns:1fr}.formbar button{width:100%}}
</style>
</head>
<body>
<header><h1>simpleagentchat</h1><span class="muted" id="status">Ready</span></header>
<main>
<section>
<h2>Chat</h2>
<iframe id="chatFrame" src="/chat.html" title="Chat transcript"></iframe>
<div class="body stack">
<textarea id="message" placeholder="Message"></textarea>
<div class="row"><label class="muted"><input id="critical" type="checkbox"> Critical</label><button class="primary" onclick="sendMessage()">Send</button><button onclick="refreshAll()">Refresh</button></div>
</div>
</section>
<section>
<div class="tabs" role="tablist">
<button id="tabRoles" aria-selected="true" onclick="showTab('roles')">Roles</button>
<button id="tabGoals" aria-selected="false" onclick="showTab('goals')">Goals</button>
<button id="tabAssets" aria-selected="false" onclick="showTab('assets')">Assets</button>
</div>
<div class="body">
<div id="rolesPanel" class="stack">
<div class="formbar"><label class="field"><span>Role</span><select id="roleSelect" onchange="loadRole()"></select></label><label class="field"><span>Name</span><input id="roleName" placeholder="new-role"></label><button onclick="saveRole()">Save</button><button class="danger" onclick="deleteRole()">Delete</button></div>
<label class="field"><span>Instructions</span><textarea id="roleInstructions"></textarea></label>
<label class="field"><span>Memory</span><textarea id="roleMemory"></textarea></label>
<div class="row"><button onclick="saveRoleInstructions()">Save instructions</button><button onclick="saveRoleMemory()">Save memory</button></div>
</div>
<div id="goalsPanel" class="stack" hidden>
<div class="formbar"><label class="field"><span>Goal</span><select id="goalSelect" onchange="loadGoal()"></select></label><label class="field"><span>Name</span><input id="goalName" placeholder="goal.md"></label><button onclick="saveGoal()">Save</button><button class="danger" onclick="deleteGoal()">Delete</button></div>
<label class="field"><span>Content</span><textarea id="goalContent"></textarea></label>
</div>
<div id="assetsPanel" class="stack" hidden>
<div class="formbar assets"><label class="field"><span>Name</span><input id="assetName" placeholder="asset.txt"></label><label class="field"><span>File</span><input id="assetFile" type="file"></label><button onclick="uploadAsset()">Upload</button></div>
<div id="assetList" class="list"></div>
</div>
</div>
</section>
</main>
<script>
const $=id=>document.getElementById(id);
function setStatus(text){$('status').textContent=text}
async function api(path, options){const r=await fetch(path, options); if(!r.ok){throw new Error(await r.text())} return r.headers.get('content-type')?.includes('json')?r.json():r.text()}
function refreshChat(){ $('chatFrame').src='/chat.html?ts='+Date.now() }
async function refreshAll(){await Promise.all([loadRoles(),loadGoals(),loadAssets()]); refreshChat(); setStatus('Refreshed')}
async function sendMessage(){await api('/api/messages',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({markdown:$('message').value,critical:$('critical').checked})}); $('message').value=''; $('critical').checked=false; await refreshAll()}
function showTab(name){for(const n of ['roles','goals','assets']){$(n+'Panel').hidden=n!==name; $('tab'+n[0].toUpperCase()+n.slice(1)).setAttribute('aria-selected',n===name)}}
async function loadRoles(){const data=await api('/api/roles'); $('roleSelect').innerHTML=data.roles.map(r=>`<option>${r.role}</option>`).join(''); if(data.roles.length) await loadRole()}
async function loadRole(){const role=$('roleSelect').value; if(!role)return; const data=await api('/api/roles/'+encodeURIComponent(role)); $('roleName').value=data.role; $('roleInstructions').value=data.instructions; $('roleMemory').value=data.memory}
async function saveRole(){const role=$('roleName').value || $('roleSelect').value; const instructions=$('roleInstructions').value; const memory=$('roleMemory').value; await api('/api/roles/'+encodeURIComponent(role)+'/instructions',{method:'PUT',headers:{'content-type':'application/json'},body:JSON.stringify({markdown:instructions})}); await api('/api/roles/'+encodeURIComponent(role)+'/memory',{method:'PUT',headers:{'content-type':'application/json'},body:JSON.stringify({markdown:memory})}); await refreshAll()}
async function saveRoleInstructions(){const role=$('roleName').value || $('roleSelect').value; await api('/api/roles/'+encodeURIComponent(role)+'/instructions',{method:'PUT',headers:{'content-type':'application/json'},body:JSON.stringify({markdown:$('roleInstructions').value})}); await loadRoles()}
async function saveRoleMemory(){const role=$('roleName').value || $('roleSelect').value; await api('/api/roles/'+encodeURIComponent(role)+'/memory',{method:'PUT',headers:{'content-type':'application/json'},body:JSON.stringify({markdown:$('roleMemory').value})}); await loadRoles()}
async function deleteRole(){const role=$('roleSelect').value; if(role){await api('/api/roles/'+encodeURIComponent(role),{method:'DELETE'}); await refreshAll()}}
async function loadGoals(){const data=await api('/api/goals'); $('goalSelect').innerHTML=data.goals.map(g=>`<option>${g.name}</option>`).join(''); if(data.goals.length) await loadGoal(); else $('goalContent').value=''}
async function loadGoal(){const name=$('goalSelect').value; if(!name)return; const data=await api('/api/goals/'+encodeURIComponent(name)); $('goalName').value=data.name; $('goalContent').value=data.content}
async function saveGoal(){const name=$('goalName').value || $('goalSelect').value; await api('/api/goals/'+encodeURIComponent(name),{method:'PUT',headers:{'content-type':'application/json'},body:JSON.stringify({content:$('goalContent').value})}); await refreshAll()}
async function deleteGoal(){const name=$('goalSelect').value; if(name){await api('/api/goals/'+encodeURIComponent(name),{method:'DELETE'}); await refreshAll()}}
async function loadAssets(){const data=await api('/api/assets'); $('assetList').innerHTML=data.assets.map(a=>`<div class="item"><a href="/assets/${encodeURIComponent(a.name)}" target="_blank">${a.name}</a><span class="muted">${a.length} bytes</span></div>`).join('')}
async function uploadAsset(){const file=$('assetFile').files[0]; const name=$('assetName').value || file?.name; if(!file||!name)return; await fetch('/api/assets/'+encodeURIComponent(name),{method:'PUT',body:file}).then(async r=>{if(!r.ok)throw new Error(await r.text())}); await refreshAll()}
refreshAll().catch(e=>setStatus(e.message));
setInterval(refreshChat,5000);
</script>
</body>
</html>
""";
}

internal static class Markdown
{
    public static string ToHtml(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var html = new StringBuilder();
        var paragraph = new StringBuilder();
        var inCode = false;
        var inList = false;

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
            {
                return;
            }

            html.Append("<p>").Append(Inline(paragraph.ToString().Trim())).AppendLine("</p>");
            paragraph.Clear();
        }

        void CloseList()
        {
            if (inList)
            {
                html.AppendLine("</ul>");
                inList = false;
            }
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                CloseList();
                if (inCode)
                {
                    html.AppendLine("</code></pre>");
                    inCode = false;
                }
                else
                {
                    html.AppendLine("<pre><code>");
                    inCode = true;
                }
                continue;
            }

            if (inCode)
            {
                html.Append(WebUtility.HtmlEncode(line)).Append('\n');
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                CloseList();
                continue;
            }

            var trimmed = line.TrimStart();
            var headingLevel = HeadingLevel(trimmed);
            if (headingLevel > 0)
            {
                FlushParagraph();
                CloseList();
                var text = trimmed[(headingLevel + 1)..].Trim();
                html.Append("<h").Append(headingLevel).Append(">")
                    .Append(Inline(text))
                    .Append("</h").Append(headingLevel).AppendLine(">");
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (!inList)
                {
                    html.AppendLine("<ul>");
                    inList = true;
                }

                html.Append("<li>").Append(Inline(trimmed[2..].Trim())).AppendLine("</li>");
                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.Append(' ');
            }

            paragraph.Append(line.Trim());
        }

        FlushParagraph();
        CloseList();
        if (inCode)
        {
            html.AppendLine("</code></pre>");
        }

        return html.ToString();
    }

    private static int HeadingLevel(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == '#')
        {
            count++;
        }

        return count is >= 1 and <= 6 && count < line.Length && line[count] == ' ' ? count : 0;
    }

    private static string Inline(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        encoded = Regex.Replace(encoded, "`([^`]+)`", "<code>$1</code>");
        encoded = Regex.Replace(encoded, "\\*\\*([^*]+)\\*\\*", "<strong>$1</strong>");
        return encoded;
    }
}

internal static class Atomic
{
    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(tempPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, path, overwrite: true);
    }

    public static async Task WriteTextAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(text);
        await writer.FlushAsync();
        stream.Flush(flushToDisk: true);
    }
}

internal static class Retry
{
    public static async Task WithinAsync(int waitMs, Func<Task> action)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        Exception? last = null;
        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }

            if (waitMs == 0 || DateTime.UtcNow >= deadline)
            {
                throw CliException.LockTimeout(last?.Message ?? "Timed out waiting for file lock.");
            }

            await Task.Delay(75);
        }
    }
}

internal static class Time
{
    public static string UtcNowRoundTrip() => RoundTrip(DateTimeOffset.UtcNow);
    public static string RoundTrip(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static string Text<T>(T value) => JsonSerializer.Serialize(value, Options);
}
}
