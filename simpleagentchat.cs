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
                throw CliException.Usage("usage", "Usage: dotnet simpleagentchat.cs <init|serve|say|fetch|goal|export-html> ...");
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
                "export-html" => await Commands.ExportHtmlAsync(rest),
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

    public static Task<int> ExportHtmlAsync(string[] args)
    {
        if (args.Length != 0)
        {
            throw CliException.Usage("usage", "Usage: dotnet simpleagentchat.cs export-html");
        }

        var root = RepositoryRoot.FindRequired();
        var workspace = ChatWorkspace.OpenExisting(root);
        HtmlViews.RegenerateChat(workspace);
        Console.Out.WriteLine(workspace.ChatHtmlPath);
        return Task.FromResult(0);
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

            if (token.StartsWith('-'))
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

            if (token.StartsWith('-'))
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

            if (token.StartsWith('-'))
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
                if (token.StartsWith('-'))
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

        if (name.StartsWith('.') ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.EndsWith('.') ||
            name.EndsWith(' '))
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
    private static readonly string[] NewlineSeparators = { "\r\n", "\n" };

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

    public static string SafeCombine(string baseDir, string childName)
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
        var hasEntry = content.Split(NewlineSeparators, StringSplitOptions.None)
            .Select(line => line.Trim())
            .Any(line => string.Equals(line, ".simpleagentchat", StringComparison.Ordinal) ||
                         string.Equals(line, ".simpleagentchat/", StringComparison.Ordinal));
        if (hasEntry)
        {
            return;
        }

        var builder = new StringBuilder(content);
        if (builder.Length > 0 && !content.EndsWith('\n') && !content.EndsWith('\r'))
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
        "reviewer" => "# Reviewer Role\n\nPerform a thorough code review. Look for gaps the implementer may have missed, flag serious concerns clearly, and if you are unsure why the implementation is done a certain way, ask a question in the chat. Fetch before meaningful review, read current goals and role memory, and mark goals done only after checking the implementation from this role's responsibility.\n",
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
            var separator = existing.EndsWith('\n') || existing.EndsWith('\r')
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
- CRITICAL: once you join, keep listening for new chat messages until the goal is done or you are explicitly instructed not to listen. If no messages are available yet, do not stop; run a long wait such as `dotnet simpleagentchat.cs fetch <nextCursor> --wait-ms 300000 --json` and repeat it after timeouts so you can respond to incoming messages.
- Read all files in `.simpleagentchat/goals/`.
- Read `.simpleagentchat/roles/<role>/instructions.md`.
- Read `.simpleagentchat/roles/<role>/role_memory.md`.
- Review prior non-system messages from your role and continue from where that role left off.
- Do not begin implementation work until a human message exactly says `Start`, unless a previous fetched human `Start` already exists.

During work:

- Fetch from your latest fetched cursor before each meaningful step.
- Keep a long-poll fetch active or repeat long waits from your latest fetched cursor until the goal is done or a fetched message explicitly tells you not to listen. A `timedOut: true` response means no message arrived during that wait, not that you may stop listening.
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
        var message = MessageStore.NewMessage(role, kind, markdown, new[] { statusPath });

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
        var system = MessageStore.NewMessage(
            "system",
            "goals.recheck",
            $"Goals changed. All current roles must re-check and re-approve `{goalName}`.",
            new[] { statusPath, RootRelative(_workspace.GoalPath(goalName)) });
        var goalMessage = MessageStore.NewMessage("goal", "chat.message", reason, Array.Empty<string>());

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

    private static GoalStatusDocument NormalizeDocument(string goalName, GoalStatusDocument document)
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
    private readonly object _eventClientsLock = new();
    private readonly List<ServerEventClient> _eventClients = new();
    private int _port;
    private int _eventSequence;

    public LocalServer(ChatWorkspace workspace, int? requestedPort, bool noOpen)
    {
        _workspace = workspace;
        _requestedPort = requestedPort;
        _noOpen = noOpen;
    }

    public async Task<int> RunAsync()
    {
        using var listener = StartListener();
        using var watchers = new DisposableWatchers(StartWatchers());
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

    private List<FileSystemWatcher> StartWatchers()
    {
        var watchers = new List<FileSystemWatcher>();
        AddWatcher(_workspace.MessagesDir, "messages", includeSubdirectories: false);
        AddWatcher(_workspace.RolesDir, "roles", includeSubdirectories: true);
        AddWatcher(_workspace.GoalsDir, "goals", includeSubdirectories: false);
        AddWatcher(_workspace.GoalStatusDir, "goals", includeSubdirectories: false);
        AddWatcher(_workspace.AssetsDir, "assets", includeSubdirectories: false);
        return watchers;

        void AddWatcher(string path, string eventName, bool includeSubdirectories)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            FileSystemEventHandler changed = (_, eventArgs) =>
            {
                if (ShouldBroadcastFileEvent(eventArgs.FullPath))
                {
                    BroadcastEvent(eventName);
                }
            };
            RenamedEventHandler renamed = (_, eventArgs) =>
            {
                if (ShouldBroadcastFileEvent(eventArgs.FullPath) || ShouldBroadcastFileEvent(eventArgs.OldFullPath))
                {
                    BroadcastEvent(eventName);
                }
            };
            watcher.Created += changed;
            watcher.Changed += changed;
            watcher.Deleted += changed;
            watcher.Renamed += renamed;
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
        }
    }

    private static bool ShouldBroadcastFileEvent(string path)
    {
        var name = Path.GetFileName(path);
        return !string.IsNullOrWhiteSpace(name) && !name.StartsWith('.');
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
            var bytes = Encoding.UTF8.GetBytes(HtmlViews.RenderChat(_workspace));
            await WriteBytesAsync(context, 200, "text/html; charset=utf-8", bytes);
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

        if (segments.Length == 2 && segments[1] == "events" && method == "GET")
        {
            await StreamEventsAsync(context);
            return;
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

    private async Task StreamEventsAsync(HttpListenerContext context)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.SendChunked = true;
        context.Response.Headers["Cache-Control"] = "no-cache";

        await using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true
        };
        var client = new ServerEventClient(writer);
        lock (_eventClientsLock)
        {
            _eventClients.Add(client);
        }

        try
        {
            await writer.WriteAsync(": connected\n\n");
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(25));
                await writer.WriteAsync(": heartbeat\n\n");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpListenerException)
        {
        }
        finally
        {
            lock (_eventClientsLock)
            {
                _eventClients.Remove(client);
            }

            client.Dispose();
        }
    }

    private void BroadcastEvent(string eventName)
    {
        ServerEventClient[] clients;
        lock (_eventClientsLock)
        {
            clients = _eventClients.ToArray();
        }

        if (clients.Length == 0)
        {
            return;
        }

        var id = Interlocked.Increment(ref _eventSequence);
        var payload = Json.Text(new { type = eventName, updatedAtUtc = Time.UtcNowRoundTrip() });
        foreach (var client in clients)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await client.WriteAsync(id, eventName, payload);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpListenerException)
                {
                    lock (_eventClientsLock)
                    {
                        _eventClients.Remove(client);
                    }
                }
            });
        }
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
        await WriteJsonAsync(context, 200, new
        {
            response.NextCursor,
            response.TimedOut,
            Messages = response.Messages.Select(ToApiMessage).ToArray()
        });
    }

    private async Task PostMessageAsync(HttpListenerContext context)
    {
        using var document = await ReadJsonDocumentAsync(context.Request);
        var markdown = GetString(document, "markdown") ?? "";
        var critical = GetBool(document, "critical") ?? false;
        if (critical && !markdown.TrimStart().StartsWith('!'))
        {
            markdown = "! " + markdown.TrimStart();
        }

        var message = await new MessageStore(_workspace).AppendAsync("human", "chat.message", markdown, Array.Empty<string>(), 30000);
        await WriteJsonAsync(context, 200, new { message = ToApiMessage(message), nextCursor = message.Id });
    }

    private static object ToApiMessage(Message message) => new
    {
        message.Id,
        message.TimestampUtc,
        message.Role,
        message.Kind,
        message.Markdown,
        Html = Markdown.ToHtml(message.Markdown),
        message.ChangedPaths
    };

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

        if (segments.Length == 2 && method == "POST")
        {
            await PostRoleAsync(context);
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

        if (segments.Length == 4 && method == "POST" && segments[3] == "rename")
        {
            await RenameRoleAsync(context, segments[2]);
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

    private async Task PostRoleAsync(HttpListenerContext context)
    {
        using var document = await ReadJsonDocumentAsync(context.Request);
        var role = GetString(document, "role") ?? "";
        if (!NameRules.IsValidRoleName(role, allowReserved: false))
        {
            throw CliException.Validation("invalid_role", "Role name is not safe.");
        }

        var dir = _workspace.RoleDirectory(role);
        if (Directory.Exists(dir))
        {
            throw new ApiException(409, "role_exists", $"Role '{role}' already exists.");
        }

        _workspace.EnsureRoleFiles(role);
        var instructionsPath = Path.Combine(dir, "instructions.md");
        var memoryPath = Path.Combine(dir, "role_memory.md");
        var instructions = GetString(document, "instructions");
        var memory = GetString(document, "memory");
        if (instructions is not null)
        {
            Atomic.WriteText(instructionsPath, instructions);
        }

        if (memory is not null)
        {
            Atomic.WriteText(memoryPath, memory);
        }

        var message = await new MessageStore(_workspace).AppendAsync(
            "system",
            "roles.changed",
            "Roles changed. Agents must re-read `.simpleagentchat/roles` before continuing.",
            new[] { RootRelative(instructionsPath), RootRelative(memoryPath) },
            30000);
        await WriteJsonAsync(context, 200, new { role, message });
    }

    private async Task RenameRoleAsync(HttpListenerContext context, string role)
    {
        if (!NameRules.IsValidRoleName(role, allowReserved: false))
        {
            throw CliException.Validation("invalid_role", "Role name is not safe.");
        }

        using var document = await ReadJsonDocumentAsync(context.Request);
        var newRole = GetString(document, "role") ?? "";
        if (!NameRules.IsValidRoleName(newRole, allowReserved: false))
        {
            throw CliException.Validation("invalid_role", "New role name is not safe.");
        }

        if (string.Equals(role, newRole, StringComparison.Ordinal))
        {
            await WriteJsonAsync(context, 200, new { role, renamed = false });
            return;
        }

        var oldDir = _workspace.RoleDirectory(role);
        var newDir = _workspace.RoleDirectory(newRole);
        if (!Directory.Exists(oldDir))
        {
            throw new ApiException(404, "missing_role", $"Role '{role}' does not exist.");
        }

        if (Directory.Exists(newDir))
        {
            throw new ApiException(409, "role_exists", $"Role '{newRole}' already exists.");
        }

        Directory.Move(oldDir, newDir);
        UpdateGoalStatusRoleName(role, newRole);
        var message = await new MessageStore(_workspace).AppendAsync(
            "system",
            "roles.changed",
            "Roles changed. Agents must re-read `.simpleagentchat/roles` before continuing.",
            new[] { RootRelative(oldDir), RootRelative(newDir) },
            30000);
        await WriteJsonAsync(context, 200, new { role = newRole, renamed = true, message });
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
                    complete = status.Complete,
                    status
                };
            }).ToArray();
            await WriteJsonAsync(context, 200, new { goals });
            return;
        }

        if (segments.Length == 2 && method == "POST")
        {
            await PostGoalAsync(context);
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

        if (segments.Length == 4 && method == "POST" && segments[3] == "rename")
        {
            await RenameGoalAsync(context, segments[2]);
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

        var status = new GoalStatusStore(_workspace).GetStatus(name);
        return new { name, content = File.ReadAllText(path, Encoding.UTF8), status };
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
        var message = MessageStore.NewMessage(
            "system",
            "goals.changed",
            "Goals changed. Agents must re-read `.simpleagentchat/goals` before continuing.",
            new[] { RootRelative(goalPath), RootRelative(statusPath) });

        await Retry.WithinAsync(30000, async () =>
        {
            Atomic.WriteText(goalPath, content);
            new GoalStatusStore(_workspace).ResetGoalForCurrentRoles(name, message.TimestampUtc, message.Id);
            await store.WriteMessageFileNoRetryAsync(message);
        });

        await WriteJsonAsync(context, 200, new { name, message });
    }

    private async Task PostGoalAsync(HttpListenerContext context)
    {
        using var document = await ReadJsonDocumentAsync(context.Request);
        var name = GetString(document, "name") ?? "";
        if (!NameRules.IsValidGoalOrAssetName(name))
        {
            throw CliException.Validation("invalid_goal", "Goal file name is not safe.");
        }

        var goalPath = _workspace.GoalPath(name);
        if (File.Exists(goalPath))
        {
            throw new ApiException(409, "goal_exists", $"Goal '{name}' already exists.");
        }

        var content = GetString(document, "content") ?? "";
        Atomic.WriteText(goalPath, content);
        var store = new MessageStore(_workspace);
        var message = MessageStore.NewMessage(
            "system",
            "goals.changed",
            "Goals changed. Agents must re-read `.simpleagentchat/goals` before continuing.",
            new[] { RootRelative(goalPath), RootRelative(_workspace.GoalStatusPath(name)) });
        new GoalStatusStore(_workspace).ResetGoalForCurrentRoles(name, message.TimestampUtc, message.Id);
        await store.AppendPreparedBatchAsync(new[] { message }, 30000);
        await WriteJsonAsync(context, 200, new { name, message });
    }

    private async Task RenameGoalAsync(HttpListenerContext context, string name)
    {
        if (!NameRules.IsValidGoalOrAssetName(name))
        {
            throw CliException.Validation("invalid_goal", "Goal file name is not safe.");
        }

        using var document = await ReadJsonDocumentAsync(context.Request);
        var newName = GetString(document, "name") ?? "";
        if (!NameRules.IsValidGoalOrAssetName(newName))
        {
            throw CliException.Validation("invalid_goal", "New goal file name is not safe.");
        }

        if (string.Equals(name, newName, StringComparison.Ordinal))
        {
            await WriteJsonAsync(context, 200, new { name, renamed = false });
            return;
        }

        var oldGoalPath = _workspace.GoalPath(name);
        var newGoalPath = _workspace.GoalPath(newName);
        if (!File.Exists(oldGoalPath))
        {
            throw new ApiException(404, "missing_goal", $"Goal '{name}' does not exist.");
        }

        if (File.Exists(newGoalPath))
        {
            throw new ApiException(409, "goal_exists", $"Goal '{newName}' already exists.");
        }

        var oldStatusPath = _workspace.GoalStatusPath(name);
        var newStatusPath = _workspace.GoalStatusPath(newName);
        if (File.Exists(newStatusPath))
        {
            throw new ApiException(409, "goal_status_exists", $"Goal status for '{newName}' already exists.");
        }

        File.Move(oldGoalPath, newGoalPath);
        if (File.Exists(oldStatusPath))
        {
            File.Move(oldStatusPath, newStatusPath);
            RewriteGoalStatusName(newStatusPath, newName);
        }

        var message = await new MessageStore(_workspace).AppendAsync(
            "system",
            "goals.changed",
            "Goals changed. Agents must re-read `.simpleagentchat/goals` before continuing.",
            new[] { RootRelative(oldGoalPath), RootRelative(newGoalPath), RootRelative(oldStatusPath), RootRelative(newStatusPath) },
            30000);
        await WriteJsonAsync(context, 200, new { name = newName, renamed = true, message });
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

        if (segments.Length == 3 && method == "DELETE")
        {
            await DeleteAssetAsync(context, segments[2]);
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

    private async Task DeleteAssetAsync(HttpListenerContext context, string name)
    {
        if (!NameRules.IsValidGoalOrAssetName(name))
        {
            throw CliException.Validation("invalid_asset", "Asset file name is not safe.");
        }

        var path = _workspace.AssetPath(name);
        if (!File.Exists(path))
        {
            throw new ApiException(404, "missing_asset", $"Asset '{name}' does not exist.");
        }

        File.Delete(path);
        await WriteJsonAsync(context, 200, new { name, deleted = true });
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

    private void UpdateGoalStatusRoleName(string oldRole, string newRole)
    {
        if (!Directory.Exists(_workspace.GoalStatusDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_workspace.GoalStatusDir, "*.status.json"))
        {
            try
            {
                var document = JsonSerializer.Deserialize<GoalStatusDocument>(File.ReadAllText(file, Encoding.UTF8), Json.Options);
                if (document?.Roles is null || !document.Roles.TryGetValue(oldRole, out var entry))
                {
                    continue;
                }

                document.Roles.Remove(oldRole);
                document.Roles[newRole] = entry;
                document.Complete = RecomputeGoalComplete(document);
                Atomic.WriteText(file, Json.Text(document) + "\n");
            }
            catch (JsonException)
            {
                continue;
            }
        }
    }

    private void RewriteGoalStatusName(string statusPath, string newName)
    {
        try
        {
            var document = JsonSerializer.Deserialize<GoalStatusDocument>(File.ReadAllText(statusPath, Encoding.UTF8), Json.Options);
            if (document is null)
            {
                return;
            }

            document.Goal = newName;
            document.Complete = RecomputeGoalComplete(document);
            Atomic.WriteText(statusPath, Json.Text(document) + "\n");
        }
        catch (JsonException)
        {
        }
    }

    private bool RecomputeGoalComplete(GoalStatusDocument document)
    {
        var roles = _workspace.GetRoleNames();
        return roles.Count > 0 &&
               roles.All(role => document.Roles.TryGetValue(role, out var entry) && entry.Status == "done");
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

internal sealed class ServerEventClient : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public ServerEventClient(StreamWriter writer)
    {
        _writer = writer;
    }

    public async Task WriteAsync(int id, string eventName, string payload)
    {
        await _writeLock.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _writer.WriteAsync($"id: {id}\n");
            await _writer.WriteAsync($"event: {eventName}\n");
            await _writer.WriteAsync("data: ");
            await _writer.WriteAsync(payload.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal));
            await _writer.WriteAsync("\n\n");
            await _writer.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
    }
}

internal sealed class DisposableWatchers : IDisposable
{
    private readonly IReadOnlyList<FileSystemWatcher> _watchers;

    public DisposableWatchers(IReadOnlyList<FileSystemWatcher> watchers)
    {
        _watchers = watchers;
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
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

    public static Message NewMessage(string role, string kind, string markdown, IReadOnlyList<string> changedPaths)
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
        Atomic.WriteText(workspace.ChatHtmlPath, RenderChat(workspace));
    }

    public static string RenderChat(ChatWorkspace workspace)
    {
        var store = new MessageStore(workspace);
        var messages = store.ReadAllMessages();
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
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
        return builder.ToString();
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
:root{color-scheme:light;--bg:#f7f7f4;--panel:#ffffff;--line:#d8d8d2;--text:#1d1d1f;--muted:#5f6368;--accent:#1b6f5c;--danger:#a83a32;--warning:#8a4600}
*{box-sizing:border-box}
[hidden]{display:none!important}
body{margin:0;background:var(--bg);color:var(--text);font-family:Segoe UI,Arial,sans-serif}
header{position:sticky;top:0;z-index:2;background:#fffffff2;border-bottom:1px solid var(--line);padding:12px 18px;display:flex;align-items:center;justify-content:space-between;gap:12px}
h1{font-size:18px;margin:0}
main{display:grid;grid-template-columns:minmax(0,1.35fr) minmax(420px,.95fr);gap:16px;padding:16px;width:100%;max-width:none}
section{background:var(--panel);border:1px solid var(--line);border-radius:8px;min-width:0}
section h2{font-size:15px;margin:0;padding:12px 14px;border-bottom:1px solid var(--line)}
.body{padding:12px 14px}
textarea,input,select{width:100%;border:1px solid var(--line);border-radius:6px;padding:9px;font:inherit;background:#fff;color:var(--text)}
textarea:focus,input:focus,select:focus{outline:2px solid #b9d7ce;outline-offset:1px}
textarea{min-height:120px;resize:vertical}
button{border:1px solid var(--line);background:#fff;border-radius:6px;padding:8px 10px;font:inherit;cursor:pointer}
button.primary{background:var(--accent);border-color:var(--accent);color:#fff}
button.danger{color:#fff;background:var(--danger);border-color:var(--danger)}
.row{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.row>*{flex:1}
.row button,.row input[type=checkbox]{flex:0 0 auto}
.chat-actions{display:flex;align-items:center;justify-content:flex-end;gap:8px;flex-wrap:wrap}
.critical-toggle{display:inline-flex;align-items:center;gap:6px;color:var(--muted);font-size:13px;user-select:none}
.critical-toggle input{width:auto;margin:0}
.fields{display:grid;gap:10px}
.field{display:grid;gap:5px}
.field span{color:var(--muted);font-size:12px;font-weight:600;text-transform:uppercase;letter-spacing:0}
.field.required>span::after{content:" *";color:var(--danger)}
.field.required input:required:invalid,.field.required select:required:invalid,.field.required textarea:required:invalid{border-color:var(--danger);box-shadow:0 0 0 1px var(--danger)}
.formbar{display:grid;grid-template-columns:minmax(130px,1fr) minmax(130px,1fr) auto auto;gap:8px;align-items:end}
.formbar.assets{grid-template-columns:minmax(130px,1fr) minmax(170px,1fr) auto}
.management{display:grid;gap:8px}
.management-fields{display:grid;grid-template-columns:minmax(160px,1fr) minmax(180px,1fr);gap:8px;align-items:end}
.management-actions{display:flex;gap:8px;justify-content:flex-end;align-items:center;flex-wrap:wrap}
.management-actions button{min-width:112px}
.tabs{display:flex;gap:6px;padding:8px;border-bottom:1px solid var(--line);flex-wrap:wrap}
.tabs button[aria-selected=true]{background:#173d36;color:#fff;border-color:#173d36}
.chat-log{height:62vh;overflow:auto;border-bottom:1px solid var(--line);background:#fff;padding:12px 14px}
.message{border-top:1px solid var(--line);padding:12px 0}
.message:first-child{border-top:0}
.message-meta{font-size:12px;color:var(--muted);display:flex;gap:8px;flex-wrap:wrap}
.message-role{font-weight:700;color:var(--accent)}
.message.system .message-role{color:#8a4600}
.message.human .message-role{color:#164c86}
.message.goal .message-role{color:#6f3d91}
.markdown{line-height:1.55}
.markdown pre{background:#202124;color:#f5f5f5;padding:10px;overflow:auto;border-radius:6px}
.markdown code{background:#ededdf;padding:1px 4px;border-radius:3px}
.markdown pre code{background:transparent;padding:0}
.list{display:grid;gap:6px;margin:8px 0}
.item{display:flex;justify-content:space-between;align-items:center;gap:8px;border:1px solid var(--line);border-radius:6px;padding:8px;background:#fff}
.muted{color:var(--muted);font-size:13px}
.stack{display:grid;gap:12px}
.goal-status-list{display:grid;gap:8px}
.goal-status-card{border:1px solid var(--line);border-radius:6px;background:#fff;overflow:hidden}
.goal-status-head{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:9px 10px;border-bottom:1px solid var(--line);font-weight:700}
.goal-status-roles{display:grid}
.goal-status-row{display:grid;grid-template-columns:minmax(120px,1fr) auto minmax(140px,1fr);gap:8px;align-items:center;padding:8px 10px;border-top:1px solid #ededdf}
.goal-status-row:first-child{border-top:0}
.status-pill{display:inline-flex;align-items:center;justify-content:center;border:1px solid var(--line);border-radius:999px;padding:2px 8px;font-size:12px;font-weight:700;white-space:nowrap}
.status-pill.done,.status-pill.complete{color:#11533f;border-color:#98c7b8;background:#eef8f3}
.status-pill.undone,.status-pill.incomplete{color:var(--warning);border-color:#e1bb7e;background:#fff7e6}
.empty{color:var(--muted);margin:0}
@media(max-width:900px){main{grid-template-columns:1fr}.chat-log{height:52vh}.formbar,.formbar.assets,.management-fields{grid-template-columns:1fr}.formbar button,.management-actions button{width:100%}.management-actions{justify-content:stretch}.goal-status-row{grid-template-columns:1fr}}
</style>
</head>
<body>
<header><h1>simpleagentchat</h1><span class="muted" id="status">Ready</span></header>
<main>
<section>
<h2>Chat</h2>
<div id="chatLog" class="chat-log" aria-live="polite"></div>
<div class="body stack">
<textarea id="message" placeholder="Message"></textarea>
<div class="chat-actions"><label class="critical-toggle"><input id="critical" type="checkbox"> Critical</label><button class="primary" onclick="sendMessage()">Send</button><button onclick="refreshAll()">Refresh</button></div>
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
<div class="management"><div class="management-fields"><label class="field required"><span>Role</span><select id="roleSelect" required onchange="loadRole()"></select></label><label class="field required"><span>Name</span><input id="roleName" required placeholder="new-role"></label></div><div class="management-actions"><button onclick="renameRole()">Rename</button><button onclick="addRole()">Add new role</button><button class="danger" onclick="deleteRole()">Delete</button></div></div>
<label class="field"><span>Instructions</span><textarea id="roleInstructions"></textarea></label>
<label class="field"><span>Memory</span><textarea id="roleMemory"></textarea></label>
<div class="row"><button onclick="saveRoleInstructions()">Save instructions</button><button onclick="saveRoleMemory()">Save memory</button></div>
</div>
<div id="goalsPanel" class="stack" hidden>
<div class="management"><div class="management-fields"><label class="field required"><span>Goal</span><select id="goalSelect" required onchange="loadGoal()"></select></label><label class="field required"><span>Name</span><input id="goalName" required placeholder="goal.md"></label></div><div class="management-actions"><button onclick="renameGoal()">Rename</button><button onclick="addGoal()">Add new goal</button><button class="danger" onclick="deleteGoal()">Delete</button></div></div>
<div id="goalStatus" class="goal-status-list" aria-live="polite"></div>
<label class="field"><span>Content</span><textarea id="goalContent"></textarea></label>
<div class="row"><button onclick="saveGoal()">Save goal</button></div>
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
let chatCursor=null;
let chatMessageIds=new Set();
function setStatus(text){$('status').textContent=text}
async function api(path, options){const r=await fetch(path, options); if(!r.ok){throw new Error(await r.text())} return r.headers.get('content-type')?.includes('json')?r.json():r.text()}
function text(value){return value==null?'':String(value)}
function roleClass(role){return text(role).replace(/[^a-zA-Z0-9_-]/g,'-')}
function chatScrollState(){const log=$('chatLog'); const remaining=log.scrollHeight-log.scrollTop-log.clientHeight; return {top:log.scrollTop,atEnd:remaining<24 || log.scrollHeight<=log.clientHeight+1}}
function restoreChatScroll(state){const log=$('chatLog'); if(state.atEnd){log.scrollTop=log.scrollHeight}else{log.scrollTop=Math.min(state.top,Math.max(0,log.scrollHeight-log.clientHeight))}}
function renderMessage(message){const article=document.createElement('article'); article.className='message '+roleClass(message.role); const meta=document.createElement('div'); meta.className='message-meta'; for(const value of [message.role,message.timestampUtc,message.kind,message.id]){const span=document.createElement('span'); span.textContent=text(value); if(value===message.role)span.className='message-role'; meta.appendChild(span)} const body=document.createElement('div'); body.className='markdown'; body.innerHTML=text(message.html); article.appendChild(meta); article.appendChild(body); return article}
function renderChat(messages,replace){const log=$('chatLog'); if(replace){log.textContent=''; chatMessageIds=new Set()} if(messages.length===0 && chatMessageIds.size===0){log.innerHTML='<p class="empty">No messages yet.</p>'; return} const empty=log.querySelector('.empty'); if(empty)empty.remove(); for(const message of messages){if(chatMessageIds.has(message.id))continue; log.appendChild(renderMessage(message)); chatMessageIds.add(message.id)}}
async function refreshChat(){const state=chatScrollState(); const data=await api('/api/messages?includeSystem=true&waitMs=0'); renderChat(data.messages||[],true); chatCursor=data.nextCursor||chatCursor; restoreChatScroll(state)}
async function loadNewMessages(){const state=chatScrollState(); const query='/api/messages?includeSystem=true&waitMs=0'+(chatCursor?'&cursor='+encodeURIComponent(chatCursor):''); const data=await api(query); renderChat(data.messages||[],!chatCursor); chatCursor=data.nextCursor||chatCursor; restoreChatScroll(state)}
async function refreshAll(){await Promise.all([loadRoles(),loadGoals(),loadAssets(),refreshChat()]); setStatus('Refreshed')}
async function sendMessage(){const state=chatScrollState(); const data=await api('/api/messages',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({markdown:$('message').value,critical:$('critical').checked})}); $('message').value=''; $('critical').checked=false; renderChat([data.message],false); chatCursor=data.nextCursor||data.message?.id||chatCursor; restoreChatScroll(state); setStatus('Sent')}
function showTab(name){for(const n of ['roles','goals','assets']){$(n+'Panel').hidden=n!==name; $('tab'+n[0].toUpperCase()+n.slice(1)).setAttribute('aria-selected',n===name)}}
function replaceOptions(select, values){select.textContent=''; for(const value of values){const option=document.createElement('option'); option.value=value; option.textContent=value; select.appendChild(option)}}
function requiredValue(id){const element=$(id); const value=element.value.trim(); if(!value){element.reportValidity?.(); element.focus(); return null} return value}
async function loadRoles(selected){const current=selected||$('roleSelect').value; const data=await api('/api/roles'); const roles=data.roles||[]; replaceOptions($('roleSelect'),roles.map(r=>r.role)); if(current && roles.some(r=>r.role===current)){$('roleSelect').value=current} if(roles.length) await loadRole(); else {$('roleName').value=''; $('roleInstructions').value=''; $('roleMemory').value=''}}
async function loadRole(){const role=$('roleSelect').value; if(!role)return; const data=await api('/api/roles/'+encodeURIComponent(role)); $('roleName').value=data.role; $('roleInstructions').value=data.instructions; $('roleMemory').value=data.memory}
async function addRole(){const role=requiredValue('roleName'); if(!role)return; await api('/api/roles',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({role,instructions:$('roleInstructions').value,memory:$('roleMemory').value})}); await loadRoles(role); await loadNewMessages()}
async function renameRole(){const role=$('roleSelect').value; const next=requiredValue('roleName'); if(!role||!next)return; await api('/api/roles/'+encodeURIComponent(role)+'/rename',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({role:next})}); await loadRoles(next); await loadNewMessages()}
async function saveRoleInstructions(){const role=$('roleSelect').value; if(!role)return; await api('/api/roles/'+encodeURIComponent(role)+'/instructions',{method:'PUT',headers:{'content-type':'application/json'},body:JSON.stringify({markdown:$('roleInstructions').value})}); await loadRoles(role); await loadNewMessages()}
async function saveRoleMemory(){const role=$('roleSelect').value; if(!role)return; await api('/api/roles/'+encodeURIComponent(role)+'/memory',{method:'PUT',headers:{'content-type':'application/json'},body:JSON.stringify({markdown:$('roleMemory').value})}); await loadRoles(role); await loadNewMessages()}
async function deleteRole(){const role=$('roleSelect').value; if(role){await api('/api/roles/'+encodeURIComponent(role),{method:'DELETE'}); await refreshAll()}}
let goalsCache=[];
function statusText(status){return status==='done'?'Done':'Incomplete'}
function statusClass(status){return status==='done'?'done':'undone'}
function renderGoalStatuses(goals){const root=$('goalStatus'); root.textContent=''; if(!goals.length){const empty=document.createElement('p'); empty.className='empty'; empty.textContent='No goals yet.'; root.appendChild(empty); return} const selected=$('goalSelect').value; for(const goal of goals){const report=goal.status||{}; const roles=Array.isArray(report.roles)?report.roles:[]; const card=document.createElement('article'); card.className='goal-status-card'; if(goal.name===selected){card.className+=' current'} const head=document.createElement('div'); head.className='goal-status-head'; const title=document.createElement('span'); title.textContent=text(goal.name); const goalPill=document.createElement('span'); goalPill.className='status-pill '+(goal.complete?'complete':'incomplete'); goalPill.textContent=goal.complete?'Complete':'Incomplete'; head.appendChild(title); head.appendChild(goalPill); card.appendChild(head); const list=document.createElement('div'); list.className='goal-status-roles'; if(roles.length===0){const row=document.createElement('div'); row.className='goal-status-row'; row.textContent='No current roles.'; list.appendChild(row)} for(const role of roles){const row=document.createElement('div'); row.className='goal-status-row'; const name=document.createElement('strong'); name.textContent=text(role.role); const pill=document.createElement('span'); pill.className='status-pill '+statusClass(role.status); pill.textContent=statusText(role.status); const updated=document.createElement('span'); updated.className='muted'; updated.textContent=role.updatedAtUtc?text(role.updatedAtUtc):'No update yet'; row.appendChild(name); row.appendChild(pill); row.appendChild(updated); list.appendChild(row)} card.appendChild(list); root.appendChild(card)}}
async function loadGoals(selected){const current=selected||$('goalSelect').value; const data=await api('/api/goals'); goalsCache=data.goals||[]; replaceOptions($('goalSelect'),goalsCache.map(g=>g.name)); if(current && goalsCache.some(g=>g.name===current)){$('goalSelect').value=current} renderGoalStatuses(goalsCache); if(goalsCache.length) await loadGoal(); else {$('goalName').value=''; $('goalContent').value=''}}
async function loadGoal(){const name=$('goalSelect').value; renderGoalStatuses(goalsCache); if(!name)return; const data=await api('/api/goals/'+encodeURIComponent(name)); $('goalName').value=data.name; $('goalContent').value=data.content; if(data.status){goalsCache=goalsCache.map(goal=>goal.name===data.name?{...goal,complete:data.status.complete,status:data.status}:goal); renderGoalStatuses(goalsCache)}}
async function addGoal(){const name=requiredValue('goalName'); if(!name)return; await api('/api/goals',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({name,content:$('goalContent').value})}); await loadGoals(name); await loadNewMessages()}
async function renameGoal(){const name=$('goalSelect').value; const next=requiredValue('goalName'); if(!name||!next)return; await api('/api/goals/'+encodeURIComponent(name)+'/rename',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({name:next})}); await loadGoals(next); await loadNewMessages()}
async function saveGoal(){const name=$('goalSelect').value; if(!name)return; await api('/api/goals/'+encodeURIComponent(name),{method:'PUT',headers:{'content-type':'application/json'},body:JSON.stringify({content:$('goalContent').value})}); await loadGoals(name); await loadNewMessages()}
async function deleteGoal(){const name=$('goalSelect').value; if(name){await api('/api/goals/'+encodeURIComponent(name),{method:'DELETE'}); await refreshAll()}}
async function loadAssets(){const data=await api('/api/assets'); $('assetList').innerHTML=data.assets.map(a=>`<div class="item"><a href="/assets/${encodeURIComponent(a.name)}" target="_blank">${a.name}</a><span class="muted">${a.length} bytes</span><button class="danger" onclick="deleteAsset('${a.name}')">Delete</button></div>`).join('')}
async function uploadAsset(){const file=$('assetFile').files[0]; const name=$('assetName').value || file?.name; if(!file||!name)return; await fetch('/api/assets/'+encodeURIComponent(name),{method:'PUT',body:file}).then(async r=>{if(!r.ok)throw new Error(await r.text())}); await refreshAll()}
async function deleteAsset(name){await api('/api/assets/'+encodeURIComponent(name),{method:'DELETE'}); await refreshAll()}
function connectEvents(){if(!window.EventSource){setInterval(()=>{loadNewMessages().catch(e=>setStatus(e.message)); loadGoals().catch(e=>setStatus(e.message))},30000); return} const events=new EventSource('/api/events'); events.onopen=()=>setStatus('Live'); events.addEventListener('messages',()=>loadNewMessages().catch(e=>setStatus(e.message))); events.addEventListener('roles',()=>{loadRoles().catch(e=>setStatus(e.message)); loadGoals().catch(e=>setStatus(e.message)); loadNewMessages().catch(e=>setStatus(e.message))}); events.addEventListener('goals',()=>{loadGoals().catch(e=>setStatus(e.message)); loadNewMessages().catch(e=>setStatus(e.message))}); events.addEventListener('assets',()=>loadAssets().catch(e=>setStatus(e.message))); events.onerror=()=>setStatus('Reconnecting')}
refreshAll().catch(e=>setStatus(e.message));
connectEvents();
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
                html.Append("<h").Append(headingLevel).Append('>')
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
        encoded = Regex.Replace(encoded, "\\[([^\\]]+)\\]\\(([^\\s)]+)\\)", match =>
        {
            var href = WebUtility.HtmlDecode(match.Groups[2].Value);
            if (!IsSafeHref(href))
            {
                return match.Value;
            }

            var attribute = WebUtility.HtmlEncode(href);
            return $"<a href=\"{attribute}\" target=\"_blank\" rel=\"noopener noreferrer\">{match.Groups[1].Value}</a>";
        });
        return encoded;
    }

    private static bool IsSafeHref(string href)
    {
        return href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               href.StartsWith("/assets/", StringComparison.Ordinal) ||
               href.StartsWith("assets/", StringComparison.Ordinal);
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
