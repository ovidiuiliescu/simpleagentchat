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
            ("initialization moves legacy root how-to-chat guide", TestInitializationMovesLegacyHowToChat),
            ("how-to-chat tells agents to keep listening", TestHowToChatRequiresListening),
            ("role join prompt clearly explains how to join", TestRoleJoinPromptIsClear),
            ("ui shell exposes dedicated add rename and asset delete controls", TestUiShellManagementControls),
            ("chat html is generated only by explicit export", TestChatHtmlExplicitExport),
            ("goal status store computes current role completion", TestGoalStatusStore),
            ("goal edit guidance resets approvals", TestGoalEditGuidanceResetsApprovals)
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
        var defaultWait = FetchArgs.Parse(new[] { cursor });
        AssertEqual(FetchArgs.DefaultWaitMs, defaultWait.WaitMs, "default fetch wait");

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
            File.WriteAllText(Path.Combine(root, "simpleagentchat.cs"), "Console.WriteLine(\"role runner smoke\");\n");
            var workspace = ChatWorkspace.Initialize(root);
            var roles = workspace.GetRoleNames();
            Assert(roles.SequenceEqual(new[] { "implementer", "reviewer" }), "default roles should be implementer and reviewer");

            var reviewerInstructions = File.ReadAllText(Path.Combine(workspace.RoleDirectory("reviewer"), "instructions.md"));
            Assert(reviewerInstructions.Contains("Perform a thorough code review.", StringComparison.Ordinal), "reviewer should ask for a thorough code review");
            Assert(reviewerInstructions.Contains("gaps the implementer may have missed", StringComparison.Ordinal), "reviewer should look for implementer gaps");
            Assert(reviewerInstructions.Contains("ask a question in the chat", StringComparison.Ordinal), "reviewer should ask questions when unclear");

            var reviewerSource = workspace.RoleSourcePath("reviewer");
            Assert(File.Exists(reviewerSource), "reviewer role source copy should exist");
            Assert(File.ReadAllText(reviewerSource).Contains("role runner smoke", StringComparison.Ordinal), "reviewer role source should match repo source");
            Assert(File.Exists(workspace.RoleRunnerDllPath("reviewer")), "reviewer runner DLL should exist");
            Assert(File.Exists(workspace.HowToChatPath), "how-to-chat guide should exist inside .simpleagentchat");
            Assert(Path.GetFullPath(workspace.HowToChatPath).StartsWith(Path.GetFullPath(workspace.ChatDir), StringComparison.OrdinalIgnoreCase), "how-to-chat guide should live under .simpleagentchat");
            Assert(!File.Exists(Path.Combine(root, "HOW_TO_CHAT.md")), "root how-to-chat guide should not be generated");
            var agentsBlock = File.ReadAllText(workspace.AgentsPath);
            Assert(agentsBlock.Contains("`.simpleagentchat/HOW_TO_CHAT.md`", StringComparison.Ordinal), "AGENTS.md should point at the room-local how-to-chat guide");
            Assert(agentsBlock.Contains("always keep polling until you are explicitly instructed not to", StringComparison.Ordinal), "AGENTS.md should include the simpleagentchat polling reminder");
            Assert(agentsBlock.Contains("If a long poll returns no messages or times out, continue polling", StringComparison.Ordinal), "AGENTS.md should mention empty long-poll responses");
            Assert(agentsBlock.Contains("always announce whenever something you do might be of meaningful interest to others", StringComparison.Ordinal), "AGENTS.md should include the meaningful-interest announcement reminder");
            Assert(agentsBlock.Contains("code changes, environment changes, test results, or other shared state changes", StringComparison.Ordinal), "AGENTS.md should include examples of meaningful-interest events");
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

    private static Task TestHowToChatRequiresListening()
    {
        var block = MarkdownBlocks.HowToChatBlock();
        Assert(block.Contains("Always run chat commands through your role-local runner", StringComparison.Ordinal), "role-local runner command guidance missing");
        Assert(block.Contains("replace it with your assigned role name", StringComparison.Ordinal), "role replacement guidance missing");
        Assert(block.Contains(".simpleagentchat/roles/<role>/simpleagentchat-<role>.cs", StringComparison.Ordinal), "role source copy guidance missing");
        Assert(block.Contains(".simpleagentchat/roles/<role>/runner/", StringComparison.Ordinal), "role runner folder guidance missing");
        Assert(block.Contains("The contention happens in the .NET file-based app build cache, not in simpleagentchat's message files", StringComparison.Ordinal), "cache contention explanation missing");
        Assert(block.Contains("agents run the already-built DLL", StringComparison.Ordinal), "already-built runner explanation missing");
        Assert(block.Contains("If your role-local runner is missing", StringComparison.Ordinal), "missing runner stop guidance missing");
        Assert(block.Contains("Do not repair `%TEMP%\\dotnet\\runfile` by hand", StringComparison.Ordinal), "runfile cache warning missing");
        Assert(block.Contains("CRITICAL: once you join, keep listening for new chat messages until a human or system message explicitly tells your role to stop listening", StringComparison.Ordinal), "join listening warning missing");
        Assert(block.Contains("Do not stop just because all current goals are done; new goals can appear after completion", StringComparison.Ordinal), "goal-completion polling warning missing");
        Assert(block.Contains("dotnet .simpleagentchat/roles/reviewer/runner/simpleagentchat-reviewer.dll fetch --wait-ms 0 --json", StringComparison.Ordinal), "initial fetch should return immediately");
        Assert(block.Contains("dotnet .simpleagentchat/roles/reviewer/runner/simpleagentchat-reviewer.dll fetch <nextCursor> --wait-ms 600000 --json", StringComparison.Ordinal), "role runner long wait example missing");
        Assert(block.Contains("repeat it after timeouts", StringComparison.Ordinal), "timeout repeat guidance missing");
        Assert(block.Contains("until a fetched message explicitly tells you not to listen", StringComparison.Ordinal), "listening stop condition missing");
        Assert(block.Contains("Goal completion is not a stop signal; continue polling after all current goals are done because new goals can be added", StringComparison.Ordinal), "post-goal polling guidance missing");
        Assert(!block.Contains("until the goal is done", StringComparison.Ordinal), "goal completion should not be a polling stop condition");
        Assert(block.Contains("inform the chat when you complete a large or important chunk of work", StringComparison.Ordinal), "important progress update guidance missing");
        Assert(block.Contains("Keep these progress updates concise and avoid spamming routine activity", StringComparison.Ordinal), "anti-spam progress guidance missing");
        Assert(block.Contains("When your role finishes its part, send a handoff message that says what changed or what work was done and what your conclusion is", StringComparison.Ordinal), "handoff clarity guidance missing");
        Assert(block.Contains("timedOut: true", StringComparison.Ordinal), "timedOut guidance missing");
        return Task.CompletedTask;
    }

    private static Task TestRoleJoinPromptIsClear()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "simpleagentchat.cs"), "Console.WriteLine(\"role runner smoke\");\n");
            var workspace = ChatWorkspace.Initialize(root);
            var prompt = RolePrompts.BuildJoinPrompt(workspace, "reviewer");

            Assert(prompt.Contains("Join the existing simpleagentchat room in this repository under the assigned role", StringComparison.Ordinal), "prompt should state the room and assigned role");
            Assert(prompt.Contains($"Repository root: {workspace.Root}", StringComparison.Ordinal), "prompt should include repo root");
            Assert(prompt.Contains("Assigned role: reviewer", StringComparison.Ordinal), "prompt should include assigned role");
            Assert(prompt.Contains("Do not run the root `simpleagentchat.cs` file for chat commands", StringComparison.Ordinal), "prompt should steer away from the root file runner");
            Assert(prompt.Contains("Read `.simpleagentchat/HOW_TO_CHAT.md`", StringComparison.Ordinal), "prompt should tell agents to read the protocol guide");
            Assert(prompt.Contains("Read the current overall goals in `.simpleagentchat/goals/`", StringComparison.Ordinal), "prompt should tell agents to read goals");
            Assert(prompt.Contains("`.simpleagentchat/roles/reviewer/instructions.md`", StringComparison.Ordinal), "prompt should include role instructions path");
            Assert(prompt.Contains("`.simpleagentchat/roles/reviewer/role_memory.md`", StringComparison.Ordinal), "prompt should include role memory path");
            Assert(prompt.Contains("fetch --wait-ms 0 --json", StringComparison.Ordinal), "prompt should include immediate first fetch command");
            Assert(prompt.Contains("announce yourself briefly", StringComparison.Ordinal), "prompt should include conditional self-announcement");
            Assert(prompt.Contains("keep polling/fetching for other messages", StringComparison.Ordinal), "prompt should include ongoing polling guidance");
            Assert(prompt.Contains("fetch <nextCursor> --wait-ms 600000 --json", StringComparison.Ordinal), "prompt should include long-poll command");
            Assert(prompt.Contains("If a long poll returns no messages or times out, continue polling", StringComparison.Ordinal), "prompt should include empty long-poll guidance");
            Assert(prompt.Contains("goal done reviewer <goal_file_name>", StringComparison.Ordinal), "prompt should include role-specific goal completion command");
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

    private static Task TestInitializationMovesLegacyHowToChat()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "simpleagentchat.cs"), "Console.WriteLine(\"role runner smoke\");\n");
            var legacyPath = Path.Combine(root, "HOW_TO_CHAT.md");
            MarkedBlock.Upsert(legacyPath, "old generated instructions\n");

            var workspace = ChatWorkspace.Initialize(root);

            Assert(File.Exists(workspace.HowToChatPath), "room-local how-to-chat guide should exist");
            Assert(!File.Exists(legacyPath), "generated-only legacy root how-to-chat guide should be deleted");

            File.WriteAllText(legacyPath, "Keep this note.\n");
            MarkedBlock.Upsert(legacyPath, "old generated instructions\n");
            ChatWorkspace.Initialize(root);

            Assert(File.Exists(legacyPath), "legacy root file with user content should be preserved");
            var legacyContent = File.ReadAllText(legacyPath);
            Assert(legacyContent.Contains("Keep this note.", StringComparison.Ordinal), "legacy user content should remain");
            Assert(!legacyContent.Contains("old generated instructions", StringComparison.Ordinal), "legacy generated block should be removed");
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
        Assert(UiShell.Html.Contains("Copy prompt", StringComparison.Ordinal), "role copy prompt button missing");
        Assert(UiShell.Html.Contains("copyRolePrompt()", StringComparison.Ordinal), "role copy prompt action missing");
        Assert(UiShell.Html.Contains("currentRolePrompt=text(data.joinPrompt)", StringComparison.Ordinal), "role prompt data binding missing");
        Assert(UiShell.Html.Contains("navigator.clipboard", StringComparison.Ordinal), "clipboard API usage missing");
        Assert(UiShell.Html.Contains("Add new goal", StringComparison.Ordinal), "goal add button missing");
        Assert(UiShell.Html.Contains("renameGoal()", StringComparison.Ordinal), "goal rename action missing");
        Assert(UiShell.Html.Contains("saveGoal()", StringComparison.Ordinal), "goal save action missing");
        Assert(UiShell.Html.Contains("deleteAsset", StringComparison.Ordinal), "asset delete action missing");
        Assert(UiShell.Html.Contains("id=\"goalStatus\"", StringComparison.Ordinal), "goal status panel missing");
        Assert(UiShell.Html.Contains("renderGoalStatuses", StringComparison.Ordinal), "goal status renderer missing");
        Assert(UiShell.Html.Contains("goal.status||{}", StringComparison.Ordinal), "goal status data binding missing");
        Assert(UiShell.Html.Contains("management-actions", StringComparison.Ordinal), "management buttons should be on a separate row");
        Assert(UiShell.Html.Contains("field required", StringComparison.Ordinal), "required field marker class missing");
        Assert(UiShell.Html.Contains("content:\" *\"", StringComparison.Ordinal), "required field star missing");
        Assert(UiShell.Html.Contains(":required:invalid", StringComparison.Ordinal), "required empty field styling missing");
        Assert(UiShell.Html.Contains("width:100%;max-width:none", StringComparison.Ordinal), "main layout should use full page width");
        Assert(UiShell.Html.Contains("EventSource('/api/events')", StringComparison.Ordinal), "live event stream missing");
        Assert(UiShell.Html.Contains("id=\"chatLog\"", StringComparison.Ordinal), "chat log container missing");
        Assert(UiShell.Html.Contains("renderChat", StringComparison.Ordinal), "client-side chat rendering missing");
        Assert(UiShell.Html.Contains("loadNewMessages", StringComparison.Ordinal), "incremental message load missing");
        Assert(UiShell.Html.Contains("chatScrollState()", StringComparison.Ordinal), "chat scroll preservation missing");
        Assert(UiShell.Html.Contains("critical-toggle", StringComparison.Ordinal), "critical checkbox styling missing");
        Assert(!UiShell.Html.Contains("<iframe", StringComparison.OrdinalIgnoreCase), "chat UI should not use iframe");
        Assert(!UiShell.Html.Contains("chatFrame", StringComparison.Ordinal), "chat UI should not depend on chatFrame");
        Assert(!UiShell.Html.Contains("onclick=\"saveRole()\"", StringComparison.Ordinal), "role save should not create implicitly");
        Assert(!UiShell.Html.Contains("formbar manage", StringComparison.Ordinal), "management buttons should not share the field row");
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

    private static async Task TestGoalEditGuidanceResetsApprovals()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = ChatWorkspace.Initialize(root);
            File.WriteAllText(workspace.GoalPath("release.md"), "# Release\n");
            var goals = new GoalStatusStore(workspace);

            await goals.MarkRoleStatusAsync("release.md", "implementer", "done", 0);
            await goals.MarkRoleStatusAsync("release.md", "reviewer", "done", 0);
            Assert(goals.GetStatus("release.md").Complete, "setup should complete the goal");

            var editedMessage = MessageStore.NewMessage("system", "goals.changed", GoalSystemMessages.Edited("release.md"), Array.Empty<string>());
            goals.ResetGoalForCurrentRoles("release.md", editedMessage.TimestampUtc, editedMessage.Id);
            var reset = goals.GetStatus("release.md");

            Assert(!reset.Complete, "editing a goal should reset completion");
            Assert(reset.Roles.All(role => role.Status == "undone"), "editing a goal should mark every role undone");
            Assert(reset.Roles.All(role => role.MessageId == editedMessage.Id), "reset statuses should point at the edit system message");
            Assert(editedMessage.Markdown.Contains("All current roles were marked undone", StringComparison.Ordinal), "edit system message should explain status reset");
            Assert(editedMessage.Markdown.Contains("reprocess the goal", StringComparison.Ordinal), "edit system message should tell agents to reprocess");
            Assert(editedMessage.Markdown.Contains("re-approve it with `goal done`", StringComparison.Ordinal), "edit system message should tell agents to re-approve");
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
