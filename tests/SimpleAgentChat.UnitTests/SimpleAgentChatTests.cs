namespace SimpleAgentChat.Tests;

using SimpleAgentChat;

internal static class SimpleAgentChatTests
{
    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Body)[]
        {
            ("safe name validation rejects reserved and escaping names", TestSafeNames),
            ("say parser keeps inline markdown literal after first token", TestSayParser),
            ("fetch parser enforces cursor and wait rules", TestFetchParser),
            ("goal parser handles status, mark, and recheck forms", TestGoalParser),
            ("serve parser handles port and browser options", TestServeParser),
            ("markdown renderer escapes raw html", TestMarkdownEscaping),
            ("markdown renderer covers common chat shapes", TestMarkdownCommonShapes),
            ("initialization creates implementer and reviewer default roles", TestDefaultRoles),
            ("ui shell exposes dedicated add rename and asset delete controls", TestUiShellManagementControls),
            ("chat html is generated only by explicit export", TestChatHtmlExplicitExport),
            ("goal status store computes current role completion", TestGoalStatusStore)
        };

        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Body();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}");
                Console.Error.WriteLine(ex);
            }
        }

        Console.WriteLine(failed == 0 ? $"All {tests.Length} unit tests passed." : $"{failed} unit tests failed.");
        return failed == 0 ? 0 : 1;
    }

    private static Task TestSafeNames()
    {
        Assert(NameRules.IsValidRoleName("implementer", allowReserved: false), "implementer should be valid");
        Assert(!NameRules.IsValidRoleName("human", allowReserved: false), "human should be reserved");
        Assert(!NameRules.IsValidRoleName("COM1", allowReserved: false), "COM1 should be rejected");
        Assert(NameRules.IsValidGoalOrAssetName("release.md"), "release.md should be valid");
        Assert(!NameRules.IsValidGoalOrAssetName("NUL.txt"), "NUL.txt should be rejected");
        Assert(!NameRules.IsValidGoalOrAssetName("../release.md"), "path traversal should be rejected");
        Assert(!NameRules.IsValidGoalOrAssetName("goal."), "trailing dot should be rejected");
        Assert(!NameRules.IsValidGoalOrAssetName("a%2fb.md"), "encoded separator should be rejected");
        return Task.CompletedTask;
    }

    private static Task TestSayParser()
    {
        var parsed = SayArgs.Parse(new[] { "implementer", "--wait-ms", "12", "hello", "--wait-ms", "1000" });
        AssertEqual("implementer", parsed.Role, "role");
        AssertEqual(12, parsed.WaitMs, "wait");
        AssertEqual("hello --wait-ms 1000", parsed.InlineMarkdown, "inline markdown");

        var dash = SayArgs.Parse(new[] { "implementer", "--not-an-option" });
        AssertEqual("--not-an-option", dash.InlineMarkdown, "dash-leading markdown");

        AssertThrows<CliException>(() => SayArgs.Parse(new[] { "implementer", "--stdin", "hello" }), "mixed sources should fail");
        return Task.CompletedTask;
    }

    private static Task TestFetchParser()
    {
        var initial = FetchArgs.Parse(new[] { "--wait-ms", "0", "--json" });
        Assert(initial.Cursor is null, "initial cursor");
        AssertEqual(0, initial.WaitMs, "initial wait");
        Assert(initial.Json, "json");
        Assert(!initial.IncludeSystem, "initial fetch filters system by default");

        const string cursor = "20260704T123456.1234567Z-implementer-a1b2c3";
        var cursorFetch = FetchArgs.Parse(new[] { cursor, "5" });
        AssertEqual(cursor, cursorFetch.Cursor, "cursor");
        AssertEqual(5, cursorFetch.WaitMs, "positional wait");
        Assert(cursorFetch.IncludeSystem, "cursor fetch includes system by default");

        AssertThrows<CliException>(() => FetchArgs.Parse(new[] { "0" }), "invalid cursor should fail");
        AssertThrows<CliException>(() => FetchArgs.Parse(new[] { cursor, "5", "--wait-ms", "6" }), "duplicate wait should fail");
        return Task.CompletedTask;
    }

    private static Task TestGoalParser()
    {
        var status = GoalArgs.Parse(new[] { "status", "release.md", "--json" });
        AssertEqual("status", status.Action, "status action");
        AssertEqual("release.md", status.GoalName, "status goal");
        Assert(status.Json, "status json");

        var done = GoalArgs.Parse(new[] { "done", "implementer", "release.md", "--wait-ms", "42" });
        AssertEqual("done", done.Action, "done action");
        AssertEqual("implementer", done.Role, "done role");
        AssertEqual(42, done.WaitMs, "done wait");

        var recheck = GoalArgs.Parse(new[] { "recheck", "release.md", "--wait-ms", "7", "--", "--reason", "text" });
        AssertEqual("recheck", recheck.Action, "recheck action");
        AssertEqual("--reason text", recheck.Reason, "recheck reason");
        AssertEqual(7, recheck.WaitMs, "recheck wait");
        return Task.CompletedTask;
    }

    private static Task TestServeParser()
    {
        var parsed = ServeArgs.Parse(new[] { "--port", "8766", "--no-open" });
        AssertEqual(8766, parsed.Port, "port");
        Assert(parsed.NoOpen, "no open");
        AssertThrows<CliException>(() => ServeArgs.Parse(new[] { "--port", "0" }), "port 0 should fail");
        AssertThrows<CliException>(() => ServeArgs.Parse(new[] { "--bogus" }), "unknown serve option should fail");
        return Task.CompletedTask;
    }

    private static Task TestMarkdownEscaping()
    {
        var html = Markdown.ToHtml("<script>alert(1)</script>\n\n- **safe**");
        Assert(!html.Contains("<script>", StringComparison.OrdinalIgnoreCase), "raw script tag should not survive");
        Assert(html.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", StringComparison.Ordinal), "script should be escaped");
        Assert(html.Contains("<strong>safe</strong>", StringComparison.Ordinal), "bold should render");
        return Task.CompletedTask;
    }

    private static Task TestMarkdownCommonShapes()
    {
        var html = Markdown.ToHtml("# Heading\n\n- item\n\n```cs\n<int>\n```\n\nUse `code`, **bold**, [asset](assets/report.md), and [bad](javascript:alert(1)).");
        Assert(html.Contains("<h1>Heading</h1>", StringComparison.Ordinal), "heading should render");
        Assert(html.Contains("<li>item</li>", StringComparison.Ordinal), "list item should render");
        Assert(html.Contains("&lt;int&gt;", StringComparison.Ordinal), "code block should escape html");
        Assert(html.Contains("<code>code</code>", StringComparison.Ordinal), "inline code should render");
        Assert(html.Contains("<strong>bold</strong>", StringComparison.Ordinal), "bold should render");
        Assert(html.Contains("<a href=\"assets/report.md\" target=\"_blank\" rel=\"noopener noreferrer\">asset</a>", StringComparison.Ordinal), "safe asset link should render");
        Assert(!html.Contains("href=\"javascript:alert(1)\"", StringComparison.OrdinalIgnoreCase), "unsafe link should not render");
        return Task.CompletedTask;
    }

    private static Task TestDefaultRoles()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = ChatWorkspace.Initialize(root);
            var roles = workspace.GetRoleNames();
            Assert(roles.SequenceEqual(new[] { "implementer", "reviewer" }), "default roles should be implementer and reviewer");

            var reviewerInstructions = File.ReadAllText(Path.Combine(workspace.RoleDirectory("reviewer"), "instructions.md"));
            Assert(reviewerInstructions.Contains("Perform a thorough code review.", StringComparison.Ordinal), "reviewer should ask for a thorough code review");
            Assert(reviewerInstructions.Contains("gaps the implementer may have missed", StringComparison.Ordinal), "reviewer should look for implementer gaps");
            Assert(reviewerInstructions.Contains("ask a question in the chat", StringComparison.Ordinal), "reviewer should ask questions when unclear");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private static Task TestUiShellManagementControls()
    {
        Assert(UiShell.Html.Contains("Add new role", StringComparison.Ordinal), "role add button missing");
        Assert(UiShell.Html.Contains("renameRole()", StringComparison.Ordinal), "role rename action missing");
        Assert(UiShell.Html.Contains("Add new goal", StringComparison.Ordinal), "goal add button missing");
        Assert(UiShell.Html.Contains("renameGoal()", StringComparison.Ordinal), "goal rename action missing");
        Assert(UiShell.Html.Contains("saveGoal()", StringComparison.Ordinal), "goal save action missing");
        Assert(UiShell.Html.Contains("deleteAsset", StringComparison.Ordinal), "asset delete action missing");
        Assert(UiShell.Html.Contains("EventSource('/api/events')", StringComparison.Ordinal), "live event stream missing");
        Assert(UiShell.Html.Contains("id=\"chatLog\"", StringComparison.Ordinal), "chat log container missing");
        Assert(UiShell.Html.Contains("renderChat", StringComparison.Ordinal), "client-side chat rendering missing");
        Assert(UiShell.Html.Contains("loadNewMessages", StringComparison.Ordinal), "incremental message load missing");
        Assert(UiShell.Html.Contains("chatScrollState()", StringComparison.Ordinal), "chat scroll preservation missing");
        Assert(UiShell.Html.Contains("critical-toggle", StringComparison.Ordinal), "critical checkbox styling missing");
        Assert(!UiShell.Html.Contains("<iframe", StringComparison.OrdinalIgnoreCase), "chat UI should not use iframe");
        Assert(!UiShell.Html.Contains("chatFrame", StringComparison.Ordinal), "chat UI should not depend on chatFrame");
        Assert(!UiShell.Html.Contains("onclick=\"saveRole()\"", StringComparison.Ordinal), "role save should not create implicitly");
        Assert(!UiShell.Html.Contains("setInterval(refreshChat,5000)", StringComparison.Ordinal), "chat should not depend on fixed refresh polling when events are available");
        return Task.CompletedTask;
    }

    private static async Task TestChatHtmlExplicitExport()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = ChatWorkspace.Initialize(root);
            Assert(!File.Exists(workspace.ChatHtmlPath), "chat.html should not be generated during init");

            await new MessageStore(workspace).AppendAsync("implementer", "chat.message", "# Exported", Array.Empty<string>(), 0);
            Assert(!File.Exists(workspace.ChatHtmlPath), "chat.html should not be generated during normal message writes");

            HtmlViews.RegenerateChat(workspace);
            var html = File.ReadAllText(workspace.ChatHtmlPath);
            Assert(html.Contains("<h1>simpleagentchat transcript</h1>", StringComparison.Ordinal), "export should include transcript title");
            Assert(html.Contains("<h1>Exported</h1>", StringComparison.Ordinal), "export should render message markdown");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestGoalStatusStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = ChatWorkspace.Initialize(root);
            File.WriteAllText(workspace.GoalPath("release.md"), "# Release\n");
            var goals = new GoalStatusStore(workspace);

            var initial = goals.GetStatus("release.md");
            Assert(!initial.Complete, "initial goal should not be complete");
            AssertEqual(2, initial.Roles.Count, "role count");
            Assert(initial.Roles.All(role => role.Status == "undone"), "initial roles should be undone");

            var implementer = await goals.MarkRoleStatusAsync("release.md", "implementer", "done", 0);
            Assert(File.Exists(Path.Combine(workspace.MessagesDir, implementer.Id + ".json")), "done message file exists");
            var partial = goals.GetStatus("release.md");
            Assert(!partial.Complete, "one role done is not complete");
            AssertEqual("done", partial.Roles.Single(role => role.Role == "implementer").Status, "implementer done");
            AssertEqual("undone", partial.Roles.Single(role => role.Role == "reviewer").Status, "reviewer undone");

            await goals.MarkRoleStatusAsync("release.md", "reviewer", "done", 0);
            var complete = goals.GetStatus("release.md");
            Assert(complete.Complete, "both roles done should complete goal");

            var recheck = await goals.RecheckAsync("release.md", "Changed requirements", 0);
            AssertEqual("system", recheck.SystemMessage.Role, "recheck system role");
            AssertEqual("goal", recheck.GoalMessage.Role, "recheck goal role");
            var reset = goals.GetStatus("release.md");
            Assert(!reset.Complete, "recheck resets completion");
            Assert(reset.Roles.All(role => role.Status == "undone"), "recheck marks all undone");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'");
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
