namespace SimpleAgentChat.Tests;

using System.Globalization;
using System.IO.Compression;
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
            ("role and asset parsers handle resource management forms", TestRoleAndAssetParsers),
            ("archive parser handles scope and import mode", TestArchiveParser),
            ("serve parser handles port and browser options", TestServeParser),
            ("markdown renderer escapes raw html", TestMarkdownEscaping),
            ("markdown renderer covers common chat shapes", TestMarkdownCommonShapes),
            ("initialization creates implementer and reviewer default roles", TestDefaultRoles),
            ("initialization moves legacy root how-to-chat guide", TestInitializationMovesLegacyHowToChat),
            ("how-to-chat explains shared runner and active-session lifecycle", TestHowToChatRequiresListening),
            ("role join prompt clearly explains how to join", TestRoleJoinPromptIsClear),
            ("resource operations manage roles goals and assets", TestResourceOperationsManageFiles),
            ("archive operations export and import selected room files", TestArchiveOperations),
            ("failed replace imports restore the previous room content", TestFailedReplaceImportRollsBack),
            ("ui shell exposes single role save, export, and management controls", TestUiShellManagementControls),
            ("chat html is generated only by explicit export", TestChatHtmlExplicitExport),
            ("cursor fetch wakes when a new message file arrives", TestCursorFetchWakesOnMessage),
            ("cursor allocation follows serialized commit order", TestCursorAllocationFollowsCommitOrder),
            ("goal status store computes current role completion", TestGoalStatusStore),
            ("concurrent goal approvals preserve every role status", TestConcurrentGoalApprovals),
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

        var join = JoinArgs.Parse(new[] { "reviewer", "--json" });
        AssertEqual("reviewer", join.Role, "join role");
        Assert(join.Json, "join json");
        AssertThrows<CliException>(() => JoinArgs.Parse(new[] { "human" }), "join should reject reserved roles");
        return Task.CompletedTask;
    }

    private static Task TestGoalParser()
    {
        var list = GoalArgs.Parse(new[] { "list", "--json" });
        AssertEqual("list", list.Action, "list action");
        Assert(list.Json, "list json");

        var add = GoalArgs.Parse(new[] { "add", "release.md", "--from", "goal.md", "--wait-ms", "9" });
        AssertEqual("add", add.Action, "add action");
        AssertEqual("release.md", add.GoalName, "add goal");
        AssertEqual("goal.md", add.FilePath, "add file");
        AssertEqual(9, add.WaitMs, "add wait");

        var update = GoalArgs.Parse(new[] { "update", "release.md", "--from", "goal2.md" });
        AssertEqual("update", update.Action, "update action");
        AssertEqual("goal2.md", update.FilePath, "update file");

        var remove = GoalArgs.Parse(new[] { "remove", "release.md", "--wait-ms", "4" });
        AssertEqual("remove", remove.Action, "remove action");
        AssertEqual(4, remove.WaitMs, "remove wait");

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
        AssertThrows<CliException>(() => GoalArgs.Parse(new[] { "add", "release.md" }), "goal add requires file");
        AssertThrows<CliException>(() => GoalArgs.Parse(new[] { "done", "implementer" }), "done requires goal");
        return Task.CompletedTask;
    }

    private static Task TestRoleAndAssetParsers()
    {
        var roleList = RoleCommandArgs.Parse(new[] { "list", "--json" });
        AssertEqual("list", roleList.Action, "role list action");
        Assert(roleList.Json, "role list json");

        var roleAdd = RoleCommandArgs.Parse(new[] { "add", "observer", "--instructions", "instructions.md", "--memory", "memory.md", "--wait-ms", "8" });
        AssertEqual("add", roleAdd.Action, "role add action");
        AssertEqual("observer", roleAdd.Role, "role add role");
        AssertEqual("instructions.md", roleAdd.InstructionsFile, "role add instructions");
        AssertEqual("memory.md", roleAdd.MemoryFile, "role add memory");
        AssertEqual(8, roleAdd.WaitMs, "role add wait");

        var roleUpdate = RoleCommandArgs.Parse(new[] { "update", "observer", "--memory", "memory.md" });
        AssertEqual("update", roleUpdate.Action, "role update action");
        AssertEqual("memory.md", roleUpdate.MemoryFile, "role update memory");

        var roleRemove = RoleCommandArgs.Parse(new[] { "remove", "observer" });
        AssertEqual("remove", roleRemove.Action, "role remove action");
        AssertEqual("observer", roleRemove.Role, "role remove role");

        var assetList = AssetCommandArgs.Parse(new[] { "list", "--json" });
        AssertEqual("list", assetList.Action, "asset list action");
        Assert(assetList.Json, "asset list json");

        var assetAdd = AssetCommandArgs.Parse(new[] { "add", "report.md", "--from", "report.md" });
        AssertEqual("add", assetAdd.Action, "asset add action");
        AssertEqual("report.md", assetAdd.Name, "asset add name");
        AssertEqual("report.md", assetAdd.FilePath, "asset add file");

        var assetUpdate = AssetCommandArgs.Parse(new[] { "update", "report.md", "--from", "updated.md" });
        AssertEqual("update", assetUpdate.Action, "asset update action");

        var assetRemove = AssetCommandArgs.Parse(new[] { "remove", "report.md" });
        AssertEqual("remove", assetRemove.Action, "asset remove action");

        AssertThrows<CliException>(() => RoleCommandArgs.Parse(new[] { "update", "observer" }), "role update requires a file option");
        AssertThrows<CliException>(() => AssetCommandArgs.Parse(new[] { "add", "report.md" }), "asset add requires file");
        return Task.CompletedTask;
    }

    private static Task TestArchiveParser()
    {
        var export = ArchiveCommandArgs.Parse(new[] { "export", "room.zip", "--roles", "--messages", "--json" });
        AssertEqual("export", export.Action, "archive export action");
        AssertEqual("room.zip", export.FilePath, "archive export path");
        Assert(export.Scope.Roles, "archive export should include roles");
        Assert(export.Scope.Messages, "archive export should include messages");
        Assert(!export.Scope.Goals, "archive export should not include unselected goals");
        Assert(export.Json, "archive export json");

        var exportDefault = ArchiveCommandArgs.Parse(new[] { "export", "room.zip" });
        Assert(exportDefault.Scope.Roles && exportDefault.Scope.Goals && exportDefault.Scope.GoalStatus && exportDefault.Scope.Messages && exportDefault.Scope.Assets, "archive export default scope should include all content");

        var importReplace = ArchiveCommandArgs.Parse(new[] { "import", "room.zip", "--replace", "--goals", "--goal-status" });
        AssertEqual("import", importReplace.Action, "archive import action");
        AssertEqual(ArchiveImportMode.Replace, importReplace.ImportMode!.Value, "archive import replace mode");
        Assert(importReplace.Scope.Goals, "archive import should include goals");
        Assert(importReplace.Scope.GoalStatus, "archive import should include goal status");
        Assert(!importReplace.Scope.Roles, "archive import should not include unselected roles");

        var importMerge = ArchiveCommandArgs.Parse(new[] { "import", "room.zip", "--mode", "merge" });
        AssertEqual(ArchiveImportMode.Merge, importMerge.ImportMode!.Value, "archive import mode option");

        AssertThrows<CliException>(() => ArchiveCommandArgs.Parse(new[] { "import", "room.zip" }), "archive import should require mode");
        AssertThrows<CliException>(() => ArchiveCommandArgs.Parse(new[] { "import", "room.zip", "--merge", "--replace" }), "archive import should reject mixed modes");
        AssertThrows<CliException>(() => ArchiveCommandArgs.Parse(new[] { "export", "room.zip", "--merge" }), "archive export should reject import mode");
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
            Assert(reviewerInstructions.Contains("keep polling every 2-3 minutes while reviewing", StringComparison.Ordinal), "reviewer should keep polling during work");
            Assert(reviewerInstructions.Contains("Announce review findings or shared-state changes", StringComparison.Ordinal), "reviewer should announce meaningful findings");

            Assert(File.Exists(workspace.RunnerSourcePath), "shared runner source copy should exist");
            Assert(File.ReadAllText(workspace.RunnerSourcePath).Contains("role runner smoke", StringComparison.Ordinal), "shared runner source should match repo source");
            Assert(File.Exists(workspace.RunnerDllPath), "shared runner DLL should exist");
            Assert(File.Exists(workspace.HowToChatPath), "how-to-chat guide should exist inside .simpleagentchat");
            Assert(Path.GetFullPath(workspace.HowToChatPath).StartsWith(Path.GetFullPath(workspace.ChatDir), StringComparison.OrdinalIgnoreCase), "how-to-chat guide should live under .simpleagentchat");
            Assert(!File.Exists(Path.Combine(root, "HOW_TO_CHAT.md")), "root how-to-chat guide should not be generated");
            var agentsBlock = File.ReadAllText(workspace.AgentsPath);
            Assert(agentsBlock.Contains("`.simpleagentchat/HOW_TO_CHAT.md`", StringComparison.Ordinal), "AGENTS.md should point at the room-local how-to-chat guide");
            Assert(agentsBlock.Contains("While actively working in simpleagentchat mode", StringComparison.Ordinal), "AGENTS.md should scope polling to active work");
            Assert(agentsBlock.Contains("perform one final fetch and then leave", StringComparison.Ordinal), "AGENTS.md should explain the session exit condition");
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
        Assert(block.Contains("Always run chat commands through the prebuilt shared runner", StringComparison.Ordinal), "shared runner command guidance missing");
        Assert(block.Contains("replace it with your assigned role name", StringComparison.Ordinal), "role replacement guidance missing");
        Assert(block.Contains(".simpleagentchat/simpleagentchat-runner.cs", StringComparison.Ordinal), "shared source copy guidance missing");
        Assert(block.Contains(".simpleagentchat/runner/", StringComparison.Ordinal), "shared runner folder guidance missing");
        Assert(block.Contains("The contention happens in the .NET file-based app build cache, not in simpleagentchat's message files", StringComparison.Ordinal), "cache contention explanation missing");
        Assert(block.Contains("one already-built runner DLL", StringComparison.Ordinal), "already-built runner explanation missing");
        Assert(block.Contains("If the shared runner is missing", StringComparison.Ordinal), "missing runner stop guidance missing");
        Assert(block.Contains("Do not repair `%TEMP%\\dotnet\\runfile` by hand", StringComparison.Ordinal), "runfile cache warning missing");
        Assert(block.Contains("join reviewer --json", StringComparison.Ordinal), "one-step join guidance missing");
        Assert(block.Contains("dotnet .simpleagentchat/runner/simpleagentchat-runner.dll fetch --wait-ms 0 --json", StringComparison.Ordinal), "initial fetch should return immediately");
        Assert(block.Contains("dotnet .simpleagentchat/runner/simpleagentchat-runner.dll fetch <nextCursor> --wait-ms 600000 --json", StringComparison.Ordinal), "shared runner long wait example missing");
        Assert(block.Contains("Continue polling while you are actively working or have been asked to monitor the room", StringComparison.Ordinal), "active polling condition missing");
        Assert(block.Contains("After the handoff, perform one final fetch", StringComparison.Ordinal), "session exit guidance missing");
        Assert(block.Contains("at least every 2-3 minutes while you are doing actual work", StringComparison.Ordinal), "during-work polling cadence missing");
        Assert(block.Contains("inform the chat when you complete a large or important chunk of work", StringComparison.Ordinal), "important progress update guidance missing");
        Assert(block.Contains("find anything of interest, are about to make a big change, or want the opinion of other participants", StringComparison.Ordinal), "meaningful interest guidance missing");
        Assert(block.Contains("Keep these updates concise and avoid spamming routine activity", StringComparison.Ordinal), "anti-spam progress guidance missing");
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
            Assert(prompt.Contains("join reviewer --json", StringComparison.Ordinal), "prompt should include the one-step join command");
            Assert(prompt.Contains("`.simpleagentchat/roles/reviewer`", StringComparison.Ordinal), "prompt should include the role directory");
            Assert(prompt.Contains("announce yourself briefly", StringComparison.Ordinal), "prompt should include conditional self-announcement");
            Assert(prompt.Contains("poll/fetch for other messages at meaningful checkpoints", StringComparison.Ordinal), "prompt should include active polling guidance");
            Assert(prompt.Contains("at least every 2-3 minutes while doing actual work", StringComparison.Ordinal), "prompt should include during-work polling cadence");
            Assert(prompt.Contains("fetch <nextCursor> --wait-ms 600000 --json", StringComparison.Ordinal), "prompt should include long-poll command");
            Assert(prompt.Contains("After your handoff, perform one final fetch", StringComparison.Ordinal), "prompt should include exit guidance");
            Assert(prompt.Contains("find anything of interest, are about to make a big change, or want the opinion of other participants", StringComparison.Ordinal), "prompt should include meaningful announcement guidance");
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

    private static async Task TestResourceOperationsManageFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "simpleagentchat.cs"), "Console.WriteLine(\"role runner smoke\");\n");
            var workspace = ChatWorkspace.Initialize(root);

            var instructionsPath = Path.Combine(root, "observer-instructions.md");
            var memoryPath = Path.Combine(root, "observer-memory.md");
            File.WriteAllText(instructionsPath, "# Observer\n\nWatch coordination.\n");
            File.WriteAllText(memoryPath, "# Role Memory\n\nRemember handoffs.\n");

            var addRoleArgs = RoleCommandArgs.Parse(new[] { "add", "observer", "--instructions", instructionsPath, "--memory", memoryPath, "--wait-ms", "0" });
            var addRoleMessages = await ResourceOperations.AddRoleAsync(workspace, addRoleArgs);
            AssertEqual(1, addRoleMessages.Count, "role add message count");
            AssertEqual("roles.changed", addRoleMessages[0].Kind, "role add message kind");
            Assert(File.ReadAllText(Path.Combine(workspace.RoleDirectory("observer"), "instructions.md")).Contains("Watch coordination.", StringComparison.Ordinal), "role instructions should be created from file");
            Assert(File.ReadAllText(Path.Combine(workspace.RoleDirectory("observer"), "role_memory.md")).Contains("Remember handoffs.", StringComparison.Ordinal), "role memory should be created from file");
            Assert(ResourceOperations.ListRoles(workspace).Any(role => role.Role == "observer"), "role list should include added role");

            var updatedMemoryPath = Path.Combine(root, "observer-memory-updated.md");
            File.WriteAllText(updatedMemoryPath, "# Role Memory\n\nUpdated durable note.\n");
            var updateRoleArgs = RoleCommandArgs.Parse(new[] { "update", "observer", "--memory", updatedMemoryPath, "--wait-ms", "0" });
            var updateRoleMessages = await ResourceOperations.UpdateRoleAsync(workspace, updateRoleArgs);
            AssertEqual(1, updateRoleMessages.Count, "role update message count");
            AssertEqual("roles.memory.changed", updateRoleMessages[0].Kind, "role memory update message kind");
            Assert(File.ReadAllText(Path.Combine(workspace.RoleDirectory("observer"), "role_memory.md")).Contains("Updated durable note.", StringComparison.Ordinal), "role memory should update");

            var saveExistingRole = await ResourceOperations.SaveRoleAsync(workspace, "observer", "# Observer\n\nSaved instructions.\n", "# Role Memory\n\nSaved memory.\n", 0, createIfMissing: false);
            Assert(!saveExistingRole.Created, "role save should overwrite existing role");
            AssertEqual("roles.changed", saveExistingRole.Message.Kind, "role save existing message kind");
            Assert(File.ReadAllText(Path.Combine(workspace.RoleDirectory("observer"), "instructions.md")).Contains("Saved instructions.", StringComparison.Ordinal), "role save should overwrite instructions");
            Assert(File.ReadAllText(Path.Combine(workspace.RoleDirectory("observer"), "role_memory.md")).Contains("Saved memory.", StringComparison.Ordinal), "role save should overwrite memory");

            var removedRole = await ResourceOperations.RemoveRoleAsync(workspace, "observer", 0);
            AssertEqual("roles.deleted", removedRole.Kind, "role remove message kind");
            Assert(!Directory.Exists(workspace.RoleDirectory("observer")), "role directory should be removed");

            var saveNewRole = await ResourceOperations.SaveRoleAsync(workspace, "observer", "# Observer\n\nNew instructions.\n", "# Role Memory\n\nNew memory.\n", 0, createIfMissing: true);
            Assert(saveNewRole.Created, "role save should create missing role");
            Assert(File.ReadAllText(Path.Combine(workspace.RoleDirectory("observer"), "instructions.md")).Contains("New instructions.", StringComparison.Ordinal), "role save should create instructions");
            Assert(File.ReadAllText(Path.Combine(workspace.RoleDirectory("observer"), "role_memory.md")).Contains("New memory.", StringComparison.Ordinal), "role save should create memory");

            var removedSavedRole = await ResourceOperations.RemoveRoleAsync(workspace, "observer", 0);
            AssertEqual("roles.deleted", removedSavedRole.Kind, "saved role remove message kind");
            Assert(!Directory.Exists(workspace.RoleDirectory("observer")), "saved role directory should be removed");

            var goalSourcePath = Path.Combine(root, "release-source.md");
            File.WriteAllText(goalSourcePath, "# Release\n\nShip it.\n");
            var addGoal = await ResourceOperations.AddGoalAsync(workspace, "release.md", goalSourcePath, 0);
            AssertEqual("goals.changed", addGoal.Kind, "goal add message kind");
            Assert(File.ReadAllText(workspace.GoalPath("release.md")).Contains("Ship it.", StringComparison.Ordinal), "goal file should be created");
            Assert(ResourceOperations.ListGoals(workspace).Any(goal => goal.Name == "release.md"), "goal list should include added goal");

            var goals = new GoalStatusStore(workspace);
            await goals.MarkRoleStatusAsync("release.md", "implementer", "done", 0);
            await goals.MarkRoleStatusAsync("release.md", "reviewer", "done", 0);
            Assert(goals.GetStatus("release.md").Complete, "goal should be complete before update");

            var updatedGoalPath = Path.Combine(root, "release-updated.md");
            File.WriteAllText(updatedGoalPath, "# Release\n\nChanged scope.\n");
            var updateGoal = await ResourceOperations.UpdateGoalAsync(workspace, "release.md", updatedGoalPath, 0);
            AssertEqual("goals.changed", updateGoal.Kind, "goal update message kind");
            Assert(updateGoal.Markdown.Contains("reprocess the goal", StringComparison.Ordinal), "goal update should ask agents to reprocess");
            var resetStatus = goals.GetStatus("release.md");
            Assert(!resetStatus.Complete, "goal update should reset completion");
            Assert(resetStatus.Roles.All(role => role.Status == "undone"), "goal update should mark all roles undone");

            var removeGoal = await ResourceOperations.RemoveGoalAsync(workspace, "release.md", 0);
            AssertEqual("goals.changed", removeGoal.Kind, "goal remove message kind");
            Assert(!File.Exists(workspace.GoalPath("release.md")), "goal file should be removed");
            Assert(!File.Exists(workspace.GoalStatusPath("release.md")), "goal status file should be removed");

            var assetSource = Path.Combine(root, "asset-source.txt");
            File.WriteAllText(assetSource, "first");
            ResourceOperations.AddAsset(workspace, "notes.txt", assetSource);
            AssertEqual("first", File.ReadAllText(workspace.AssetPath("notes.txt")), "asset should be added");
            Assert(ResourceOperations.ListAssets(workspace).Any(asset => asset.Name == "notes.txt"), "asset list should include added asset");

            var assetUpdate = Path.Combine(root, "asset-updated.txt");
            File.WriteAllText(assetUpdate, "second");
            ResourceOperations.UpdateAsset(workspace, "notes.txt", assetUpdate);
            AssertEqual("second", File.ReadAllText(workspace.AssetPath("notes.txt")), "asset should be updated");

            ResourceOperations.RemoveAsset(workspace, "notes.txt");
            Assert(!File.Exists(workspace.AssetPath("notes.txt")), "asset should be removed");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestArchiveOperations()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(root, "source");
        var destinationRoot = Path.Combine(root, "destination");
        var mergeRoot = Path.Combine(root, "merge");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);
        Directory.CreateDirectory(mergeRoot);
        try
        {
            var source = ChatWorkspace.Initialize(sourceRoot);
            source.EnsureRoleFiles("observer");
            File.WriteAllText(Path.Combine(source.RoleDirectory("observer"), "instructions.md"), "# Observer\n\nWatch imports.\n");
            File.WriteAllText(source.GoalPath("release.md"), "# Release\n\nShip it.\n");
            File.WriteAllText(source.AssetPath("notes.txt"), "source asset");
            await new MessageStore(source).AppendAsync("implementer", "chat.message", "source message", Array.Empty<string>(), 0);
            await new GoalStatusStore(source).MarkRoleStatusAsync("release.md", "implementer", "done", 0);

            var zipPath = Path.Combine(root, "room.zip");
            var export = ArchiveOperations.ExportToFile(source, zipPath, ArchiveScope.All);
            Assert(File.Exists(zipPath), "archive zip should be created");
            Assert(export.Counts.Roles >= 6, "archive should include role instruction and memory files");
            AssertEqual(1, export.Counts.Goals, "archive should include one goal");
            AssertEqual(1, export.Counts.GoalStatus, "archive should include one goal status file");
            Assert(export.Counts.Messages >= 2, "archive should include chat and goal status messages");
            AssertEqual(1, export.Counts.Assets, "archive should include one asset");

            var destination = ChatWorkspace.Initialize(destinationRoot);
            destination.EnsureRoleFiles("legacy");
            File.WriteAllText(destination.GoalPath("old.md"), "# Old\n");
            File.WriteAllText(destination.AssetPath("old.txt"), "old asset");
            await new MessageStore(destination).AppendAsync("implementer", "chat.message", "old message", Array.Empty<string>(), 0);

            var imported = ArchiveOperations.ImportFromFile(destination, zipPath, ArchiveImportMode.Replace, ArchiveScope.All);
            AssertEqual(export.Counts.Goals, imported.Counts.Goals, "replace import goal count");
            Assert(File.Exists(destination.GoalPath("release.md")), "replace import should copy selected goals");
            Assert(!File.Exists(destination.GoalPath("old.md")), "replace import should remove existing selected goals");
            Assert(File.Exists(destination.GoalStatusPath("release.md")), "replace import should copy goal status");
            Assert(File.Exists(destination.AssetPath("notes.txt")), "replace import should copy selected assets");
            Assert(!File.Exists(destination.AssetPath("old.txt")), "replace import should remove existing selected assets");
            Assert(Directory.Exists(destination.RoleDirectory("observer")), "replace import should copy selected roles");
            Assert(!Directory.Exists(destination.RoleDirectory("legacy")), "replace import should remove existing selected roles");
            Assert(File.ReadAllText(Path.Combine(destination.RoleDirectory("observer"), "instructions.md")).Contains("Watch imports.", StringComparison.Ordinal), "role instructions should round trip");
            var messages = new MessageStore(destination).ReadAllMessages();
            Assert(messages.Any(message => message.Markdown == "source message"), "replace import should copy selected messages");
            Assert(!messages.Any(message => message.Markdown == "old message"), "replace import should remove existing selected messages");

            var merge = ChatWorkspace.Initialize(mergeRoot);
            var goalOnly = new ArchiveScope(false, true, false, false, false);
            ArchiveOperations.ImportFromFile(merge, zipPath, ArchiveImportMode.Merge, goalOnly);
            Assert(File.Exists(merge.GoalPath("release.md")), "merge import should copy selected goals");
            Assert(!File.Exists(merge.AssetPath("notes.txt")), "merge import should not copy unselected assets");
            Assert(!new MessageStore(merge).ReadAllMessages().Any(message => message.Markdown == "source message"), "merge import should not copy unselected messages");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task TestFailedReplaceImportRollsBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = ChatWorkspace.Initialize(root);
            File.WriteAllText(workspace.GoalPath("existing.md"), "# Existing\n");
            File.WriteAllText(workspace.AssetPath("existing.txt"), "keep me");

            var zipPath = Path.Combine(root, "invalid-room.zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("goals/replacement.md").Open()))
                {
                    writer.Write("# Replacement\n");
                }

                using var oversized = archive.CreateEntry("assets/oversized.bin", CompressionLevel.Fastest).Open();
                var buffer = new byte[64 * 1024];
                var remaining = AssetLimits.MaxBytes + 1;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(buffer.Length, remaining);
                    oversized.Write(buffer, 0, count);
                    remaining -= count;
                }
            }

            var scope = new ArchiveScope(false, true, false, false, true);
            AssertThrows<CliException>(
                () => ArchiveOperations.ImportFromFile(workspace, zipPath, ArchiveImportMode.Replace, scope),
                "oversized archive import should fail");
            Assert(File.Exists(workspace.GoalPath("existing.md")), "failed replace import should restore the existing goal");
            AssertEqual("keep me", File.ReadAllText(workspace.AssetPath("existing.txt")), "failed replace import should restore the existing asset");
            Assert(!File.Exists(workspace.GoalPath("replacement.md")), "failed replace import should not leave staged goals behind");
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
        Assert(UiShell.Html.Contains("onclick=\"exportChat()\"", StringComparison.Ordinal), "chat export button missing");
        Assert(UiShell.Html.Contains("api('/api/export-html',{method:'POST'}", StringComparison.Ordinal), "chat export endpoint call missing");
        Assert(UiShell.Html.Contains("id=\"saveRoleButton\"", StringComparison.Ordinal), "role save button missing");
        Assert(UiShell.Html.Contains("onclick=\"saveRole()\"", StringComparison.Ordinal), "role save action missing");
        Assert(UiShell.Html.Contains("id=\"addRoleButton\"", StringComparison.Ordinal), "role add button missing");
        Assert(UiShell.Html.Contains("Add new", StringComparison.Ordinal), "role add label missing");
        Assert(UiShell.Html.Contains("id=\"renameRoleButton\"", StringComparison.Ordinal), "role rename button missing");
        Assert(UiShell.Html.Contains("Rename existing", StringComparison.Ordinal), "role rename label missing");
        Assert(UiShell.Html.Contains("updateRoleButtonState()", StringComparison.Ordinal), "role dirty-state action missing");
        Assert(UiShell.Html.Contains("clearRoleFields()", StringComparison.Ordinal), "role clear action missing");
        Assert(UiShell.Html.Contains("confirm('Clear role fields?')", StringComparison.Ordinal), "role clear confirmation missing");
        Assert(UiShell.Html.Contains("roleNames.has(draft.role)", StringComparison.Ordinal), "role name existence check missing");
        Assert(UiShell.Html.Contains("draft.role===roleBaseline.role", StringComparison.Ordinal), "role rename same-name disable check missing");
        Assert(UiShell.Html.Contains("const role=roleBaseline.role", StringComparison.Ordinal), "role save should target selected role");
        Assert(UiShell.Html.Contains("method:'PUT'", StringComparison.Ordinal), "role save should use update endpoint");
        Assert(UiShell.Html.Contains("method:'POST'", StringComparison.Ordinal), "role add should use create endpoint");
        Assert(UiShell.Html.Contains("renameRole()", StringComparison.Ordinal), "role rename action missing");
        Assert(UiShell.Html.Contains("Copy prompt", StringComparison.Ordinal), "role copy prompt button missing");
        Assert(UiShell.Html.Contains("copyRolePrompt()", StringComparison.Ordinal), "role copy prompt action missing");
        Assert(UiShell.Html.Contains("currentRolePrompt=text(data.joinPrompt)", StringComparison.Ordinal), "role prompt data binding missing");
        Assert(UiShell.Html.Contains("navigator.clipboard", StringComparison.Ordinal), "clipboard API usage missing");
        Assert(UiShell.Html.Contains("Add new goal", StringComparison.Ordinal), "goal add button missing");
        Assert(UiShell.Html.Contains("renameGoal()", StringComparison.Ordinal), "goal rename action missing");
        Assert(UiShell.Html.Contains("saveGoal()", StringComparison.Ordinal), "goal save action missing");
        Assert(UiShell.Html.Contains("deleteAsset", StringComparison.Ordinal), "asset delete action missing");
        Assert(UiShell.Html.Contains("tabArchive", StringComparison.Ordinal), "import/export tab missing");
        Assert(UiShell.Html.Contains("archiveGoalStatus", StringComparison.Ordinal), "archive goal status checkbox missing");
        Assert(UiShell.Html.Contains("downloadArchive()", StringComparison.Ordinal), "archive export action missing");
        Assert(UiShell.Html.Contains("importArchive()", StringComparison.Ordinal), "archive import action missing");
        Assert(UiShell.Html.Contains("/api/archive/export?", StringComparison.Ordinal), "archive export endpoint call missing");
        Assert(UiShell.Html.Contains("/api/archive/import?", StringComparison.Ordinal), "archive import endpoint call missing");
        Assert(UiShell.Html.Contains("archiveModeReplace", StringComparison.Ordinal), "archive replace mode control missing");
        Assert(UiShell.Html.Contains("confirm(prompt)", StringComparison.Ordinal), "archive import confirmation missing");
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
        Assert(!UiShell.Html.Contains("Add new role", StringComparison.Ordinal), "role add button should be replaced by single save");
        Assert(!UiShell.Html.Contains("Save instructions", StringComparison.Ordinal), "separate instruction save should be removed");
        Assert(!UiShell.Html.Contains("Save memory", StringComparison.Ordinal), "separate memory save should be removed");
        Assert(!UiShell.Html.Contains("saveRoleInstructions", StringComparison.Ordinal), "separate instruction save action should be removed");
        Assert(!UiShell.Html.Contains("saveRoleMemory", StringComparison.Ordinal), "separate memory save action should be removed");
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

    private static async Task TestCursorFetchWakesOnMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = ChatWorkspace.Initialize(root);
            var store = new MessageStore(workspace);
            var first = await store.AppendAsync("implementer", "chat.message", "first", Array.Empty<string>(), 0);
            var fetchTask = store.FetchAsync(first.Id, includeSystem: true, waitMs: 5000);
            await Task.Delay(100);
            var second = await store.AppendAsync("reviewer", "chat.message", "second", Array.Empty<string>(), 5000);
            var response = await fetchTask;

            Assert(!response.TimedOut, "new message should wake the cursor fetch");
            AssertEqual(second.Id, response.NextCursor, "cursor should advance to the new message");
            AssertEqual("second", response.Messages.Single().Markdown, "fetch should return only the newer message");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TestCursorAllocationFollowsCommitOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = ChatWorkspace.Initialize(root);
            var store = new MessageStore(workspace);
            using var appendStarted = new ManualResetEventSlim(false);
            var mutationLock = WorkspaceLock.Acquire(workspace, 0);
            try
            {
                var appendTask = Task.Run(async () =>
                {
                    appendStarted.Set();
                    return await store.AppendAsync("implementer", "chat.message", "serialized after barrier", Array.Empty<string>(), 5000);
                });

                appendStarted.Wait();
                await Task.Delay(100);

                var future = DateTimeOffset.UtcNow.AddMinutes(5);
                var barrierId = future.ToString("yyyyMMdd'T'HHmmss.fffffff'Z'", CultureInfo.InvariantCulture) + "-system-abcdef123456";
                var barrier = new Message(
                    barrierId,
                    Time.RoundTrip(future),
                    "system",
                    "test.barrier",
                    "committed first",
                    Array.Empty<string>());
                await store.WriteMessageFileNoRetryAsync(barrier);
                MessageStore.AdvanceCursorHighWaterFromMessagesUnderLock(workspace);

                mutationLock.Dispose();
                var appended = await appendTask;
                Assert(string.CompareOrdinal(appended.Id, barrier.Id) > 0, "message cursor should follow serialized commit order");

                var fetched = await store.FetchAsync(barrier.Id, includeSystem: true, waitMs: 0);
                AssertEqual(appended.Id, fetched.Messages.Single().Id, "fetch after the barrier should include the later commit");
            }
            finally
            {
                mutationLock.Dispose();
            }
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

    private static async Task TestConcurrentGoalApprovals()
    {
        var root = Path.Combine(Path.GetTempPath(), "simpleagentchat-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = ChatWorkspace.Initialize(root);
            File.WriteAllText(workspace.GoalPath("concurrent.md"), "# Concurrent\n");
            var goals = new GoalStatusStore(workspace);

            for (var iteration = 0; iteration < 20; iteration++)
            {
                goals.ResetGoalForCurrentRoles("concurrent.md");
                using var start = new ManualResetEventSlim(false);
                var implementer = Task.Run(async () =>
                {
                    start.Wait();
                    await goals.MarkRoleStatusAsync("concurrent.md", "implementer", "done", 5000);
                });
                var reviewer = Task.Run(async () =>
                {
                    start.Wait();
                    await goals.MarkRoleStatusAsync("concurrent.md", "reviewer", "done", 5000);
                });
                start.Set();
                await Task.WhenAll(implementer, reviewer);

                var status = goals.GetStatus("concurrent.md");
                Assert(status.Complete, $"concurrent approvals should both survive iteration {iteration}");
            }
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

            Message editedMessage;
            using (WorkspaceLock.Acquire(workspace, 0))
            {
                editedMessage = new MessageStore(workspace).NewMessageUnderLock(
                    "system",
                    "goals.changed",
                    GoalSystemMessages.Edited("release.md"),
                    Array.Empty<string>());
            }
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
