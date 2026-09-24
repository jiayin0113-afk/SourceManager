namespace SourceManager;

public class Program
{
    /// <summary>Single source of truth for the reported version.</summary>
    public const string Version = "1.0.0";

    private static ArgParser _rootParser = null!;
    private static bool _noPager;

    public static int Main(string[] args)
    {
        JsonOut.Reset();
        Terminal.ResetDiagnostics();

        // Wire the library's logging seam to this CLI's terminal. Without this the engine (LibSM)
        // is silent, which is exactly right for an embedder but wrong for the `sm` front end.
        SmLog.ErrorHandler = Terminal.WriteError;
        SmLog.WarningHandler = Terminal.WriteWarning;
        SmLog.SuccessHandler = Terminal.WriteSuccess;
        SmLog.LineHandler = (text, color, bold) => Terminal.WriteLine(text, (Terminal.Color)(int)color, bold);

        ParseResult? parsed = null;
        string commandName = "sm";

        try
        {
            SetupRootParser();

            if (args.Length == 0)
            {
                Console.WriteLine(_rootParser.GetHelpText());
                return 0;
            }

            var result = _rootParser.Parse(args);
            parsed = result;
            if (result.Command != null)
                commandName = result.Command.Name;

            if (IsVersionRequested(result))
            {
                Console.WriteLine($"sm version {Version}");
                return 0;
            }

            if (result.ShowHelp)
            {
                ShowHelp(result);
                return 0;
            }

            if (result.Errors.Count > 0)
            {
                foreach (string error in result.Errors)
                    Terminal.WriteError($"error: {error}");
                if (JsonRequested(result))
                    JsonOut.WriteError(commandName, string.Join("; ", result.Errors), "usage");
                return 1;
            }

            if (result.UnknownCommand != null)
            {
                string message = $"'{result.UnknownCommand}' is not a SourceManager command. See 'sm help'.";
                Terminal.WriteError($"sm: {message}");
                if (JsonRequested(result))
                    JsonOut.WriteError(commandName, message, "unknown-command");
                return 1;
            }

            if (result.Command == null)
            {
                Console.WriteLine(_rootParser.GetHelpText());
                return 0;
            }

            ApplyColorConfig();

            int code = ExecuteCommand(result.Command, result.SubResult ?? new ParseResult());
            EmitJsonError(result, commandName, code);
            return code;
        }
        catch (InvalidOperationException ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            EmitJsonError(parsed, commandName, 128);
            return 128;
        }
        catch (Exception ex)
        {
            Terminal.WriteError($"fatal: {ex.Message}");
            EmitJsonError(parsed, commandName, 128);
            return 128;
        }
    }

    /// <summary>True when JSON output was requested, on the root or the subcommand result.</summary>
    private static bool JsonRequested(ParseResult result)
        => JsonOut.Requested(result) || (result.SubResult != null && JsonOut.Requested(result.SubResult));

    /// <summary>
    /// Emits an error envelope for a failed command that did not already produce JSON, so a
    /// --json caller always receives exactly one machine-readable document.
    /// </summary>
    private static void EmitJsonError(ParseResult? result, string commandName, int code)
    {
        if (code == 0 || result == null || !JsonRequested(result) || JsonOut.Emitted) return;
        string message = Terminal.LastError ?? "command failed";
        JsonOut.WriteError(commandName, message, code == 128 ? "fatal" : "error");
    }

    /// <summary>Applies the repository's color.ui setting before any output is produced.</summary>
    private static void ApplyColorConfig()
    {
        try
        {
            var repo = Repository.Find();
            string? setting = repo != null
                ? repo.Config.Get("color", "ui")
                : new ConfigStore(Environment.CurrentDirectory).Get("color", "ui");
            Terminal.ApplyColorSetting(setting);
        }
        catch
        {
            // Repository lookup is best-effort here; the command reports its own errors.
        }
    }

    private static void SetupRootParser()
    {
        _rootParser = new ArgParser("sm", "SourceManager - Next Generation Version Control System");

        _rootParser.AddAlias("co", "checkout");
        _rootParser.AddAlias("ci", "commit");
        _rootParser.AddAlias("st", "status");
        _rootParser.AddAlias("br", "branch");
        _rootParser.AddAlias("cp", "cherry-pick");
        _rootParser.AddAlias("rb", "rebase");

        // Declared BEFORE the commands so every subcommand inherits them: machine-readable
        // output is a cross-cutting contract, not a per-command feature.
        _rootParser.AddOption("json", null, "Emit a machine-readable JSON document", type: ArgOptionType.Bool);
        _rootParser.AddOption("format", null, "Output format (text, json)");

        _rootParser.AddCommand("init", "Initialize a new SourceManager repository", p =>
        {
            p.AddOption("bare", "B", "Create a bare repository", type: ArgOptionType.Bool);
            p.AddOption("branch", "b", "Set initial branch name", defaultValue: "main");
            p.AddOption("template", null, "Template directory to use");
            p.AddOption("separate-git-dir", null, "Separate .sm directory location");
        }).Handler = InitCmd.Execute;

        _rootParser.AddCommand("add", "Add file contents to the index", p =>
        {
            p.AddOption("all", "A", "Stage all changes (tracked and untracked)", type: ArgOptionType.Bool);
            p.AddOption("update", "u", "Stage only tracked files", type: ArgOptionType.Bool);
            p.AddOption("patch", "p", "Interactively stage hunks", type: ArgOptionType.Bool);
            p.AddOption("force", "f", "Allow adding ignored files", type: ArgOptionType.Bool);
            p.AddOption("dry-run", "n", "Don't actually add files", type: ArgOptionType.Bool);
            p.AddOption("verbose", "v", "Be verbose", type: ArgOptionType.Bool);
            p.AddOption("intent-to-add", "N", "Record intent to add later", type: ArgOptionType.Bool);
            p.AddOption("refresh", null, "Don't add, only refresh index", type: ArgOptionType.Bool);
            p.AddOption("ignore-errors", null, "Ignore errors", type: ArgOptionType.Bool);
            p.AddOption("renormalize", null, "Reapply smattributes", type: ArgOptionType.Bool);
            p.AddOption("chmod", null, "Override executable bit");
        }).Handler = AddCmd.Execute;

        _rootParser.AddCommand("commit", "Record changes to the repository", p =>
        {
            p.AddOption("message", "m", "Commit message");
            p.AddOption("all", "a", "Stage all modified files before commit", type: ArgOptionType.Bool);
            p.AddOption("amend", null, "Amend previous commit", type: ArgOptionType.Bool);
            p.AddOption("no-edit", null, "Use previous commit message", type: ArgOptionType.Bool);
            p.AddOption("allow-empty", null, "Allow empty commits", type: ArgOptionType.Bool);
            p.AddOption("allow-empty-message", null, "Allow empty commit message", type: ArgOptionType.Bool);
            p.AddOption("no-verify", "n", "Bypass pre-commit hooks", type: ArgOptionType.Bool);
            p.AddOption("signoff", "s", "Add Signed-off-by trailer", type: ArgOptionType.Bool);
            p.AddOption("no-signoff", null, "Do not add Signed-off-by", type: ArgOptionType.Bool);
            p.AddOption("date", null, "Override the author date");
            p.AddOption("author", null, "Override the author");
            p.AddOption("verbose", "v", "Show diff in commit message editor", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Suppress summary message", type: ArgOptionType.Bool);
            p.AddOption("only", "o", "Commit only specified files", type: ArgOptionType.Bool);
            p.AddOption("include", "i", "Include specified files", type: ArgOptionType.Bool);
        }).Handler = CommitCmd.Execute;

        _rootParser.AddCommand("status", "Show the working tree status", p =>
        {
            p.AddOption("short", "s", "Give output in short format", type: ArgOptionType.Bool);
            p.AddOption("branch", "b", "Show branch information", type: ArgOptionType.Bool);
            p.AddOption("porcelain", null, "Machine-readable output (v1)", type: ArgOptionType.Bool);
            p.AddOption("porcelain-v2", null, "Machine-readable output v2", type: ArgOptionType.Bool);
            p.AddOption("long", null, "Long format (default)", type: ArgOptionType.Bool);
            p.AddOption("verbose", "v", "Be verbose", type: ArgOptionType.Bool);
            p.AddOption("untracked-files", "u", "Show untracked files", defaultValue: "normal");
            p.AddOption("ignore-submodules", null, "Ignore submodules", defaultValue: "all");
            p.AddOption("ignored", null, "Show ignored files too", type: ArgOptionType.Bool);
            p.AddOption("column", null, "Display in columns");
        }).Handler = StatusCmd.Execute;

        _rootParser.AddCommand("log", "Show commit logs", p =>
        {
            p.AddOption("max-count", "n", "Limit number of commits", type: ArgOptionType.Int);
            p.AddOption("skip", null, "Skip number of commits", type: ArgOptionType.Int);
            p.AddOption("oneline", null, "One line per commit", type: ArgOptionType.Bool);
            p.AddOption("graph", null, "Show commit graph", type: ArgOptionType.Bool);
            p.AddOption("decorate", null, "Show ref names", defaultValue: "auto");
            p.AddOption("all", null, "Show all branches", type: ArgOptionType.Bool);
            p.AddOption("branches", null, "Show specific branches");
            p.AddOption("remotes", null, "Show remote-tracking branches");
            p.AddOption("tags", null, "Show tags", type: ArgOptionType.Bool);
            p.AddOption("diff-filter", null, "Filter diff output");
            p.AddOption("author", null, "Filter by author");
            p.AddOption("committer", null, "Filter by committer");
            p.AddOption("grep", null, "Filter by message pattern");
            p.AddOption("since", null, "Show commits after date");
            p.AddOption("until", null, "Show commits before date");
            p.AddOption("first-parent", null, "Follow only first parent", type: ArgOptionType.Bool);
            p.AddOption("no-merges", null, "Don't show merge commits", type: ArgOptionType.Bool);
            p.AddOption("merges", null, "Show only merge commits", type: ArgOptionType.Bool);
            p.AddOption("follow", null, "Follow file history across renames", type: ArgOptionType.Bool);
            p.AddOption("reverse", null, "Reverse order", type: ArgOptionType.Bool);
            p.AddOption("patch", "p", "Show patch output", type: ArgOptionType.Bool);
            p.AddOption("stat", null, "Show diffstat", type: ArgOptionType.Bool);
            p.AddOption("shortstat", null, "Show short diffstat", type: ArgOptionType.Bool);
            p.AddOption("reflog", "g", "Show reflog entries", type: ArgOptionType.Bool);
        }).Handler = LogCmd.Execute;

        _rootParser.AddCommand("diff", "Show changes between commits, index, or working tree", p =>
        {
            p.AddOption("cached", null, "Show staged changes (--cached)", type: ArgOptionType.Bool);
            p.AddOption("staged", null, "Show staged changes (synonym for --cached)", type: ArgOptionType.Bool);
            p.AddOption("name-only", null, "Show only names", type: ArgOptionType.Bool);
            p.AddOption("name-status", null, "Show names with status", type: ArgOptionType.Bool);
            p.AddOption("stat", null, "Show diffstat", type: ArgOptionType.Bool);
            p.AddOption("shortstat", null, "Show short diffstat", type: ArgOptionType.Bool);
            p.AddOption("patch", "p", "Generate patch", type: ArgOptionType.Bool);
            p.AddOption("no-patch", "s", "Suppress patch output", type: ArgOptionType.Bool);
            p.AddOption("raw", null, "Raw diff format", type: ArgOptionType.Bool);
            p.AddOption("word-diff", null, "Word diff");
            p.AddOption("color-words", null, "Word diff with color");
            p.AddOption("no-index", null, "Compare two paths on filesystem", type: ArgOptionType.Bool);
            p.AddOption("binary", null, "Show binary diffs", type: ArgOptionType.Bool);
            p.AddOption("text", "a", "Treat all files as text", type: ArgOptionType.Bool);
            p.AddOption("ignore-space-change", "b", "Ignore whitespace changes", type: ArgOptionType.Bool);
            p.AddOption("ignore-all-space", "w", "Ignore all whitespace", type: ArgOptionType.Bool);
            p.AddOption("ignore-blank-lines", null, "Ignore blank lines", type: ArgOptionType.Bool);
            p.AddOption("unified", "U", "Lines of context", type: ArgOptionType.Int, defaultValue: "3");
            p.AddOption("minimal", null, "Spend extra time for minimal diff", type: ArgOptionType.Bool);
            p.AddOption("patience", null, "Patience diff algorithm", type: ArgOptionType.Bool);
            p.AddOption("histogram", null, "Histogram diff algorithm", type: ArgOptionType.Bool);
            p.AddOption("diff-algorithm", null, "Diff algorithm to use");
        }).Handler = DiffCmd.Execute;

        _rootParser.AddCommand("branch", "List, create, or delete branches", p =>
        {
            p.AddOption("list", "l", "List branches", type: ArgOptionType.Bool);
            p.AddOption("all", "a", "List all branches (local and remote)", type: ArgOptionType.Bool);
            p.AddOption("remote", "r", "List remote-tracking branches", type: ArgOptionType.Bool);
            p.AddOption("delete", "d", "Delete branch", type: ArgOptionType.Bool);
            p.AddOption("force", "D", "Force delete branch", type: ArgOptionType.Bool);
            p.AddOption("move", "m", "Rename branch and its reflog");
            p.AddOption("copy", "c", "Copy a branch");
            p.AddOption("set-upstream-to", "u", "Set upstream tracking");
            p.AddOption("unset-upstream", null, "Unset upstream tracking", type: ArgOptionType.Bool);
            p.AddOption("merged", null, "Show branches merged to HEAD");
            p.AddOption("no-merged", null, "Show branches not merged to HEAD");
            p.AddOption("verbose", "v", "Show SHA1 and commit subject", type: ArgOptionType.Bool);
            p.AddOption("sort", null, "Sort order");
            p.AddOption("color", null, "Color output", defaultValue: "auto");
        }).Handler = BranchCmd.Execute;

        _rootParser.AddCommand("checkout", "Switch branches or restore working tree files", p =>
        {
            p.AddOption("branch", "b", "Create and checkout new branch");
            p.AddOption("force", "f", "Force checkout", type: ArgOptionType.Bool);
            p.AddOption("merge", "m", "Perform 3-way merge with new branch", type: ArgOptionType.Bool);
            p.AddOption("ours", null, "Ours merge strategy for checkout", type: ArgOptionType.Bool);
            p.AddOption("theirs", null, "Theirs merge strategy for checkout", type: ArgOptionType.Bool);
            p.AddOption("orphan", null, "Create orphan branch");
            p.AddOption("detach", null, "Detach HEAD at named commit", type: ArgOptionType.Bool);
            p.AddOption("track", "t", "Set upstream tracking", type: ArgOptionType.Bool);
            p.AddOption("no-track", null, "Do not set upstream tracking", type: ArgOptionType.Bool);
            p.AddOption("recurse-submodules", null, "Recurse into submodules");
        }).Handler = CheckoutCmd.Execute;

        _rootParser.AddCommand("merge", "Join two or more development histories", p =>
        {
            p.AddOption("strategy", "s", "Merge strategy to use");
            p.AddOption("no-ff", null, "Create merge commit even if fast-forward", type: ArgOptionType.Bool);
            p.AddOption("ff-only", null, "Refuse to merge unless fast-forward", type: ArgOptionType.Bool);
            p.AddOption("ff", null, "Allow fast-forward", type: ArgOptionType.Bool);
            p.AddOption("squash", null, "Squash changes into single commit", type: ArgOptionType.Bool);
            p.AddOption("no-commit", null, "Don't auto-commit merge", type: ArgOptionType.Bool);
            p.AddOption("abort", null, "Abort the current merge", type: ArgOptionType.Bool);
            p.AddOption("continue", null, "Continue the current merge", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Be quiet", type: ArgOptionType.Bool);
            p.AddOption("verbose", "v", "Be verbose", type: ArgOptionType.Bool);
            p.AddOption("message", "m", "Merge commit message");
            p.AddOption("no-verify", "n", "Skip pre-merge hook", type: ArgOptionType.Bool);
        }).Handler = MergeCmd.Execute;

        _rootParser.AddCommand("remote", "Manage tracked repositories", p =>
        {
            p.AddOption("verbose", "v", "Be verbose", type: ArgOptionType.Bool);
        }).Handler = RemoteCmd.Execute;

        _rootParser.AddCommand("push", "Update remote refs along with associated objects", p =>
        {
            p.AddOption("all", null, "Push all branches", type: ArgOptionType.Bool);
            p.AddOption("force", "f", "Force push", type: ArgOptionType.Bool);
            p.AddOption("force-with-lease", null, "Force push with lease");
            p.AddOption("delete", "d", "Delete remote ref", type: ArgOptionType.Bool);
            p.AddOption("tags", null, "Push tags", type: ArgOptionType.Bool);
            p.AddOption("follow-tags", null, "Push relevant tags", type: ArgOptionType.Bool);
            p.AddOption("dry-run", "n", "Dry run", type: ArgOptionType.Bool);
            p.AddOption("porcelain", null, "Machine-readable output", type: ArgOptionType.Bool);
            p.AddOption("set-upstream", "u", "Set upstream tracking", type: ArgOptionType.Bool);
            p.AddOption("no-verify", null, "Bypass the pre-push hook", type: ArgOptionType.Bool);
            p.AddOption("verbose", "v", "Be verbose", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Be quiet", type: ArgOptionType.Bool);
        }).Handler = PushCmd.Execute;

        _rootParser.AddCommand("pull", "Fetch from and integrate with another repository", p =>
        {
            p.AddOption("rebase", "r", "Rebase instead of merge", type: ArgOptionType.Bool);
            p.AddOption("no-rebase", null, "Merge instead of rebase", type: ArgOptionType.Bool);
            p.AddOption("ff-only", null, "Fast-forward only", type: ArgOptionType.Bool);
            p.AddOption("no-ff", null, "No fast-forward", type: ArgOptionType.Bool);
            p.AddOption("squash", null, "Squash changes", type: ArgOptionType.Bool);
            p.AddOption("force", "f", "Force pull", type: ArgOptionType.Bool);
            p.AddOption("all", null, "Fetch all remotes", type: ArgOptionType.Bool);
            p.AddOption("verbose", "v", "Be verbose", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Be quiet", type: ArgOptionType.Bool);
        }).Handler = PullCmd.Execute;

        _rootParser.AddCommand("fetch", "Download objects and refs from another repository", p =>
        {
            p.AddOption("all", null, "Fetch all remotes", type: ArgOptionType.Bool);
            p.AddOption("force", "f", "Force fetch", type: ArgOptionType.Bool);
            p.AddOption("tags", "t", "Fetch tags", type: ArgOptionType.Bool);
            p.AddOption("no-tags", "n", "Don't fetch tags", type: ArgOptionType.Bool);
            p.AddOption("prune", "p", "Prune deleted refs", type: ArgOptionType.Bool);
            p.AddOption("dry-run", null, "Dry run", type: ArgOptionType.Bool);
            p.AddOption("verbose", "v", "Be verbose", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Be quiet", type: ArgOptionType.Bool);
            p.AddOption("depth", null, "Deepen/shallow history", type: ArgOptionType.Int);
        }).Handler = FetchCmd.Execute;

        _rootParser.AddCommand("tag", "Create, list, or delete tags", p =>
        {
            p.AddOption("list", "l", "List tags", type: ArgOptionType.Bool);
            p.AddOption("delete", "d", "Delete tag", type: ArgOptionType.Bool);
            p.AddOption("annotate", "a", "Create annotated tag", type: ArgOptionType.Bool);
            p.AddOption("sign", "s", "Create signed tag", type: ArgOptionType.Bool);
            p.AddOption("force", "f", "Force create/delete", type: ArgOptionType.Bool);
            p.AddOption("message", "m", "Tag message");
            p.AddOption("sort", null, "Sort order: refname, creatordate, version:refname (prefix - to reverse)");
            // No -n short name: -n is already spoken for by nothing here, but keeping the long
            // form explicit avoids colliding with future numeric short options.
            p.AddOption("lines", "n", "Show this many annotation lines per tag", type: ArgOptionType.Int);
        }).Handler = TagCmd.Execute;

        _rootParser.AddCommand("stash", "Stash changes in a dirty working directory", p =>
        {
            p.AddOption("list", null, "List stashes", type: ArgOptionType.Bool);
            p.AddOption("show", null, "Show stash contents", type: ArgOptionType.Bool);
            p.AddOption("pop", null, "Pop (apply + drop) stash", type: ArgOptionType.Bool);
            p.AddOption("apply", null, "Apply stash", type: ArgOptionType.Bool);
            p.AddOption("drop", null, "Drop stash", type: ArgOptionType.Bool);
            p.AddOption("branch", null, "Create branch from stash");
            p.AddOption("push", null, "Push (save) stash (default)", type: ArgOptionType.Bool);
            p.AddOption("keep-index", null, "Keep index changes", type: ArgOptionType.Bool);
            p.AddOption("include-untracked", "u", "Include untracked files", type: ArgOptionType.Bool);
            p.AddOption("all", "a", "Include ignored + untracked", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Be quiet", type: ArgOptionType.Bool);
            p.AddOption("message", "m", "Stash message");
        }).Handler = StashCmd.Execute;

        _rootParser.AddCommand("reset", "Reset current HEAD to specified state", p =>
        {
            p.AddOption("soft", null, "Keep index and working tree", type: ArgOptionType.Bool);
            p.AddOption("mixed", null, "Reset index, keep working tree (default)", type: ArgOptionType.Bool);
            p.AddOption("hard", null, "Reset index and working tree", type: ArgOptionType.Bool);
            p.AddOption("merge", null, "Reset and abort merge", type: ArgOptionType.Bool);
            p.AddOption("keep", null, "Keep local changes", type: ArgOptionType.Bool);
            p.AddOption("patch", "p", "Interactive reset", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Be quiet", type: ArgOptionType.Bool);
        }).Handler = ResetCmd.Execute;

        _rootParser.AddCommand("revert", "Revert existing commits", p =>
        {
            p.AddOption("no-commit", "n", "Don't auto-commit", type: ArgOptionType.Bool);
            p.AddOption("no-edit", null, "Don't edit commit message", type: ArgOptionType.Bool);
            p.AddOption("signoff", "s", "Add Signed-off-by", type: ArgOptionType.Bool);
            p.AddOption("mainline", "m", "Mainline parent number", type: ArgOptionType.Int);
            p.AddOption("continue", null, "Continue revert", type: ArgOptionType.Bool);
            p.AddOption("abort", null, "Abort revert", type: ArgOptionType.Bool);
            p.AddOption("quit", null, "Quit revert", type: ArgOptionType.Bool);
        }).Handler = RevertCmd.Execute;

        _rootParser.AddCommand("cherry-pick", "Apply changes from existing commits", p =>
        {
            p.AddOption("no-commit", "n", "Don't auto-commit", type: ArgOptionType.Bool);
            p.AddOption("edit", "e", "Edit commit message", type: ArgOptionType.Bool);
            p.AddOption("signoff", "s", "Add Signed-off-by", type: ArgOptionType.Bool);
            p.AddOption("mainline", "m", "Mainline parent number", type: ArgOptionType.Int);
            p.AddOption("continue", null, "Continue cherry-pick", type: ArgOptionType.Bool);
            p.AddOption("abort", null, "Abort cherry-pick", type: ArgOptionType.Bool);
            p.AddOption("quit", null, "Quit cherry-pick", type: ArgOptionType.Bool);
            p.AddOption("strategy", "X", "Merge strategy option");
            p.AddOption("allow-empty", null, "Allow empty commits", type: ArgOptionType.Bool);
            p.AddOption("allow-empty-message", null, "Allow empty message", type: ArgOptionType.Bool);
        }).Handler = CherryPickCmd.Execute;

        _rootParser.AddCommand("rebase", "Reapply commits on top of another base tip", p =>
        {
            p.AddOption("onto", null, "Rebase onto given base");
            p.AddOption("interactive", "i", "Interactive rebase", type: ArgOptionType.Bool);
            p.AddOption("continue", null, "Continue rebase", type: ArgOptionType.Bool);
            p.AddOption("abort", null, "Abort rebase", type: ArgOptionType.Bool);
            p.AddOption("skip", null, "Skip current patch", type: ArgOptionType.Bool);
            p.AddOption("quit", null, "Quit rebase", type: ArgOptionType.Bool);
            p.AddOption("force", "f", "Force rebase", type: ArgOptionType.Bool);
            p.AddOption("preserve-merges", "p", "Preserve merge commits", type: ArgOptionType.Bool);
            p.AddOption("rebase-merges", "r", "Rebase merge commits", type: ArgOptionType.Bool);
            p.AddOption("strategy", "s", "Merge strategy");
            p.AddOption("autosquash", null, "Auto-squash fixup commits", type: ArgOptionType.Bool);
            p.AddOption("autostash", null, "Auto-stash before rebase", type: ArgOptionType.Bool);
            p.AddOption("no-autostash", null, "Don't auto-stash", type: ArgOptionType.Bool);
            p.AddOption("root", null, "Rebase all reachable commits", type: ArgOptionType.Bool);
        }).Handler = RebaseCmd.Execute;

        _rootParser.AddCommand("bisect", "Binary search for bugs", p =>
        {
            p.AddOption("start", null, "Start bisect session", type: ArgOptionType.Bool);
            p.AddOption("good", null, "Mark commit as good");
            p.AddOption("bad", null, "Mark commit as bad");
            p.AddOption("skip", null, "Skip commit", type: ArgOptionType.Bool);
            p.AddOption("reset", null, "Reset bisect session", type: ArgOptionType.Bool);
            p.AddOption("replay", null, "Replay bisect log");
            p.AddOption("log", null, "Show bisect log", type: ArgOptionType.Bool);
            p.AddOption("run", null, "Run bisect script");
            p.AddOption("terms", null, "Custom bisect terms");
        }).Handler = BisectCmd.Execute;

        _rootParser.AddCommand("blame", "Show revision and author for each line of a file", p =>
        {
            p.AddOption("line-porcelain", null, "Machine-readable output", type: ArgOptionType.Bool);
            p.AddOption("incremental", null, "Incremental output", type: ArgOptionType.Bool);
            p.AddOption("show-email", "e", "Show author email", type: ArgOptionType.Bool);
            p.AddOption("show-name", "f", "Show filename in origin", type: ArgOptionType.Bool);
            p.AddOption("show-number", "n", "Show line number in origin", type: ArgOptionType.Bool);
            p.AddOption("abbrev", "L", "Starting/ending line range");
            p.AddOption("reverse", null, "Reverse history", type: ArgOptionType.Bool);
        }).Handler = BlameCmd.Execute;

        _rootParser.AddCommand("config", "Get and set repository or global options", p =>
        {
            p.AddOption("global", null, "Use global config", type: ArgOptionType.Bool);
            p.AddOption("local", null, "Use local config", type: ArgOptionType.Bool);
            p.AddOption("system", null, "Use system config", type: ArgOptionType.Bool);
            p.AddOption("list", "l", "List all config", type: ArgOptionType.Bool);
            p.AddOption("get", null, "Get config value", type: ArgOptionType.Bool);
            p.AddOption("unset", null, "Remove config entry", type: ArgOptionType.Bool);
            p.AddOption("add", null, "Add config entry", type: ArgOptionType.Bool);
            p.AddOption("get-all", null, "Get all matching values", type: ArgOptionType.Bool);
            p.AddOption("type", "t", "Value type (bool, int, path)");
            p.AddOption("null", "z", "NUL-terminated output", type: ArgOptionType.Bool);
        }).Handler = ConfigCmd.Execute;

        _rootParser.AddCommand("gc", "Cleanup unnecessary files and optimize the repository", p =>
        {
            p.AddOption("aggressive", null, "Aggressively optimize", type: ArgOptionType.Bool);
            p.AddOption("auto", null, "Auto mode", type: ArgOptionType.Bool);
            p.AddOption("prune", null, "Prune loose objects", defaultValue: "2.weeks.ago");
            p.AddOption("no-prune", null, "Don't prune", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Be quiet", type: ArgOptionType.Bool);
        }).Handler = GcCmd.Execute;

        _rootParser.AddCommand("fsck", "Verify database connectivity and validity", p =>
        {
            p.AddOption("full", null, "Full check", type: ArgOptionType.Bool);
            p.AddOption("strict", null, "Strict mode", type: ArgOptionType.Bool);
            p.AddOption("unreachable", null, "Show unreachable objects", type: ArgOptionType.Bool);
            p.AddOption("dangling", null, "Show dangling objects", type: ArgOptionType.Bool);
            p.AddOption("no-reflogs", null, "Don't consider reflogs", type: ArgOptionType.Bool);
            p.AddOption("verbose", "v", "Be verbose", type: ArgOptionType.Bool);
        }).Handler = FsckCmd.Execute;

        _rootParser.AddCommand("reflog", "Manage reflog information", p =>
        {
            p.AddOption("expire", null, "Expire entries older than <time> (default 90.days.ago)");
            p.AddOption("expire-unreachable", null, "Expire unreachable entries older than <time> (default 30.days.ago)");
            p.AddOption("delete", null, "Delete reflog entries", type: ArgOptionType.Bool);
            p.AddOption("all", null, "Process all refs", type: ArgOptionType.Bool);
            p.AddOption("dry-run", null, "Show what would be done without writing", type: ArgOptionType.Bool);
            p.AddOption("rewrite", null, "Rewrite the reflog after deleting", type: ArgOptionType.Bool);
            p.AddOption("updateref", null, "Update the ref to the surviving tip after deletion", type: ArgOptionType.Bool);
            p.AddOption("max-count", "n", "Limit number of entries", type: ArgOptionType.Int);
            p.AddOption("verbose", "v", "Be verbose", type: ArgOptionType.Bool);
        }).Handler = ReflogCmd.Execute;

        _rootParser.AddCommand("serve", "Start SourceManager server (native sm:// protocol)", p =>
        {
            p.AddOption("port", null, "Port to listen on", type: ArgOptionType.Int, defaultValue: "9418");
            // HttpListener rejects "0.0.0.0" outright; "+" is its wildcard for all interfaces.
            p.AddOption("bind", null, "Address to bind to (use + for all interfaces)", defaultValue: "+");
            p.AddOption("stdio", null, "Serve over stdin/stdout (for SSH compatibility)", type: ArgOptionType.Bool);
            p.AddOption("http", null, "Serve over HTTP (legacy compatibility carrier)", type: ArgOptionType.Bool);
            p.AddOption("path", null, "Repository path");
        }).Handler = ServeCmd.Execute;

        // ─── Working-tree file operations ───────────────────────────────────────────
        _rootParser.AddCommand("mv", "Move or rename a tracked file and update the index", p =>
        {
            p.AddOption("force", "f", "Overwrite the destination if it exists", type: ArgOptionType.Bool);
            p.AddOption("dry-run", "n", "Only show what would be renamed", type: ArgOptionType.Bool);
            p.AddOption("verbose", "v", "Report each rename", type: ArgOptionType.Bool);
        }).Handler = MvCmd.Execute;

        _rootParser.AddCommand("rm", "Remove files from the working tree and the index", p =>
        {
            p.AddOption("cached", null, "Remove from the index only, keep the file", type: ArgOptionType.Bool);
            p.AddOption("force", "f", "Remove despite staged or local modifications", type: ArgOptionType.Bool);
            p.AddOption("recursive", "r", "Remove directories recursively", type: ArgOptionType.Bool);
            p.AddOption("dry-run", "n", "Only show what would be removed", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Suppress per-file output", type: ArgOptionType.Bool);
        }).Handler = RmCmd.Execute;

        _rootParser.AddCommand("clean", "Remove untracked files from the working tree", p =>
        {
            p.AddOption("force", "f", "Actually remove (required unless --dry-run)", type: ArgOptionType.Bool);
            p.AddOption("dry-run", "n", "Only show what would be removed", type: ArgOptionType.Bool);
            p.AddOption("directories", "d", "Also remove empty directories", type: ArgOptionType.Bool);
            p.AddOption("ignored", "x", "Also remove ignored files", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Suppress per-file output", type: ArgOptionType.Bool);
        }).Handler = CleanCmd.Execute;

        // ─── Inspection and plumbing ────────────────────────────────────────────────
        _rootParser.AddCommand("show", "Show a commit, tag, tree or blob", p =>
        {
            p.AddOption("stat", null, "Show a diffstat instead of the patch", type: ArgOptionType.Bool);
            p.AddOption("name-only", null, "Show only changed file names", type: ArgOptionType.Bool);
            p.AddOption("name-status", null, "Show changed file names with status", type: ArgOptionType.Bool);
            p.AddOption("patch", "p", "Show the patch (default)", type: ArgOptionType.Bool);
        }).Handler = ShowCmd.Execute;

        _rootParser.AddCommand("ls-files", "List the files tracked in the index", p =>
        {
            p.AddOption("cached", "c", "Show cached (tracked) files", type: ArgOptionType.Bool);
            p.AddOption("modified", "m", "Show files with unstaged modifications", type: ArgOptionType.Bool);
            p.AddOption("deleted", "d", "Show files deleted in the working tree", type: ArgOptionType.Bool);
            p.AddOption("others", "o", "Show untracked files", type: ArgOptionType.Bool);
            p.AddOption("stage", "s", "Show mode, object id and stage", type: ArgOptionType.Bool);
            p.AddOption("null", "z", "NUL-terminate output", type: ArgOptionType.Bool);
        }).Handler = LsFilesCmd.Execute;

        // ─── Plumbing: scriptable, read-mostly primitives ───────────────────────────
        _rootParser.AddCommand("rev-parse", "Resolve revisions to object ids", p =>
        {
            p.AddOption("verify", null, "Silence diagnostics; signal only through the exit code", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Suppress error messages", type: ArgOptionType.Bool);
            p.AddOption("short", null, "Abbreviate ids to N characters", type: ArgOptionType.Int);
            p.AddOption("abbrev-ref", null, "Show the branch name for HEAD", type: ArgOptionType.Bool);
            p.AddOption("show-toplevel", null, "Print the working tree root", type: ArgOptionType.Bool);
            p.AddOption("is-inside-work-tree", null, "Print true when inside a working tree", type: ArgOptionType.Bool);
        }).Handler = RevParseCmd.Execute;

        _rootParser.AddCommand("cat-file", "Read an object from the database", p =>
        {
            p.AddOption("type", "t", "Print the object type", type: ArgOptionType.Bool);
            p.AddOption("size", "s", "Print the object size", type: ArgOptionType.Bool);
            p.AddOption("pretty", "p", "Pretty-print the object", type: ArgOptionType.Bool);
            p.AddOption("exists", "e", "Exit 0 when the object exists", type: ArgOptionType.Bool);
            p.AddOption("batch", null, "Read object names from stdin", type: ArgOptionType.Bool);
        }).Handler = CatFileCmd.Execute;

        _rootParser.AddCommand("hash-object", "Compute the object id of content", p =>
        {
            p.AddOption("write", "w", "Also store the object", type: ArgOptionType.Bool);
            p.AddOption("stdin", null, "Read content from standard input", type: ArgOptionType.Bool);
            p.AddOption("type", "t", "Object type to hash as", defaultValue: "blob");
        }).Handler = HashObjectCmd.Execute;

        _rootParser.AddCommand("ls-tree", "List the contents of a tree object", p =>
        {
            p.AddOption("recursive", "r", "Recurse into subtrees", type: ArgOptionType.Bool);
            p.AddOption("name-only", null, "Show only names", type: ArgOptionType.Bool);
            p.AddOption("null", "z", "NUL-terminate output", type: ArgOptionType.Bool);
        }).Handler = LsTreeCmd.Execute;

        _rootParser.AddCommand("switch", "Switch branches (no file restoration)", p =>
        {
            p.AddOption("branch", "c", "Create and switch to a new branch");
            p.AddOption("force", "f", "Switch even with local changes", type: ArgOptionType.Bool);
            p.AddOption("detach", "d", "Detach HEAD at the given commit", type: ArgOptionType.Bool);
            p.AddOption("orphan", null, "Create an orphan branch");
        }).Handler = SwitchCmd.Execute;

        _rootParser.AddCommand("restore", "Restore working tree files or index entries", p =>
        {
            p.AddOption("staged", "S", "Restore the index instead of the working tree", type: ArgOptionType.Bool);
            p.AddOption("worktree", "W", "Restore the working tree", type: ArgOptionType.Bool);
            p.AddOption("source", "s", "Restore from this revision");
        }).Handler = RestoreCmd.Execute;

        _rootParser.AddCommand("merge-base", "Find the common ancestor of two commits", p =>
        {
            p.AddOption("is-ancestor", null, "Test ancestry instead of printing the base", type: ArgOptionType.Bool);
            p.AddOption("all", "a", "Show all best common ancestors", type: ArgOptionType.Bool);
        }).Handler = MergeBaseCmd.Execute;

        _rootParser.AddCommand("describe", "Name a commit by its nearest tag", p =>
        {
            p.AddOption("tags", null, "Only use tags as candidates", type: ArgOptionType.Bool);
            p.AddOption("always", null, "Fall back to the abbreviated id when no tag matches", type: ArgOptionType.Bool);
            p.AddOption("abbrev", null, "Abbreviated id length", type: ArgOptionType.Int, defaultValue: "7");
        }).Handler = DescribeCmd.Execute;

        _rootParser.AddCommand("shortlog", "Summarise history by author", p =>
        {
            p.AddOption("numbered", "n", "Prefix each author with their commit count", type: ArgOptionType.Bool);
            p.AddOption("summary", "s", "Suppress commit subjects", type: ArgOptionType.Bool);
            p.AddOption("since", null, "Only count commits after this date");
        }).Handler = ShortlogCmd.Execute;

        _rootParser.AddCommand("archive", "Export a tree as a zip or tar stream", p =>
        {
            p.AddOption("format", null, "Archive format: zip or tar", defaultValue: "zip");
            p.AddOption("output", "o", "Write to a file instead of stdout");
            p.AddOption("prefix", null, "Path prefix inside the archive");
        }).Handler = ArchiveCmd.Execute;

        _rootParser.AddCommand("help", "Show help for a command (sm help <command>)", p =>
        {
            p.AddOption("all", "a", "List every command", type: ArgOptionType.Bool);
        }).Handler = HelpCmd.Execute;

        _rootParser.AddCommand("verify", "Verify repository integrity (every object matches its id)", p =>
        {
            p.AddOption("unreachable", null, "Also report objects unreachable from any ref", type: ArgOptionType.Bool);
            p.AddOption("full", null, "Full check, including reachability", type: ArgOptionType.Bool);
            p.AddOption("quiet", "q", "Only report problems", type: ArgOptionType.Bool);
        }).Handler = VerifyCmd.Execute;

        _rootParser.AddCommand("version", "Show version information", p =>
        {
        }).Handler = _ =>
        {
            Console.WriteLine($"sm version {Version}");
            return 0;
        };

        _rootParser.AddOption("version", null, "Print version information", type: ArgOptionType.Bool);
        _rootParser.AddOption("no-pager", null, "Do not pipe output into a pager", type: ArgOptionType.Bool);
        _rootParser.AddOption("work-tree", null, "Set the path to the working tree");
        _rootParser.AddOption("sm-dir", null, "Set the path to the .sm directory");
        _rootParser.AddOption("bare", null, "Treat repository as bare", type: ArgOptionType.Bool);
        _rootParser.AddOption("exec-path", null, "Path to sm executable");
        _rootParser.AddOption("html-path", null, "Print path to HTML docs", type: ArgOptionType.Bool);
        _rootParser.AddOption("namespace", null, "Set sm namespace");
        _rootParser.AddOption("paginate", "p", "Pipe output into pager", type: ArgOptionType.Bool);
        _rootParser.AddOption("no-replace-objects", null, "Don't use replacement refs", type: ArgOptionType.Bool);
    }

    /// <summary>Looks up a registered command, used by the help command.</summary>
    public static ArgCommand? FindCommand(string name)
        => _rootParser.Commands.FirstOrDefault(
            c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Prints root help, or one command's help when a topic is given.</summary>
    public static void PrintHelp(string? topic)
    {
        if (topic == null)
        {
            Console.WriteLine(_rootParser.GetHelpText());
            return;
        }

        var command = FindCommand(topic);
        if (command?.Parser != null)
            Console.WriteLine(command.Parser.GetHelpText());
    }

    private static bool IsVersionRequested(ParseResult result)
    {
        return result.GetBoolOption("version") ||
               (result.SubResult?.GetBoolOption("version") ?? false);
    }

    private static int ExecuteCommand(ArgCommand command, ParseResult result)
    {
        if (result.ShowHelp && command.Parser != null)
        {
            Console.WriteLine(command.Parser.GetHelpText());
            return 0;
        }

        if (command.Handler != null)
            return command.Handler(result);
        return 0;
    }

    private static void ShowHelp(ParseResult result)
    {
        if (result.SubResult != null && result.Command?.Parser != null)
        {
            Console.WriteLine(result.Command.Parser.GetHelpText());
        }
        else if (result.Command?.Parser != null)
        {
            Console.WriteLine(result.Command.Parser.GetHelpText());
        }
        else
        {
            Console.WriteLine(_rootParser.GetHelpText());
        }
    }
}