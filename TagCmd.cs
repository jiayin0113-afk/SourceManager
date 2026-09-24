namespace SourceManager;

public static class TagCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();

        bool list = args.GetBoolOption("list");
        bool delete = args.GetBoolOption("delete");
        bool annotate = args.GetBoolOption("annotate");
        bool sign = args.GetBoolOption("sign");
        bool force = args.GetBoolOption("force");
        string? message = args.GetOption("message");
        string? sortOption = args.GetOption("sort");

        string? tagName = args.GetPositional(0);
        string? target = args.GetPositional(1);

        if (delete)
        {
            if (tagName == null)
            {
                Terminal.WriteError("error: tag name required");
                return 1;
            }
            return DeleteTag(repo, tagName);
        }

        if (list || (tagName == null && !delete))
        {
            return ListTags(repo, sortOption, JsonOut.Requested(args),
                args.GetIntOption("lines") ?? args.GetIntOption("n") ?? 0);
        }

        return CreateTag(repo, tagName!, target, annotate || sign, message, force);
    }

    /// <summary>
    /// Lists tags, one per line by default.
    ///
    /// This used to print a multi-line block (Tagger/Date/message) for every annotated tag, so
    /// `sm tag -l` was not a list at all and could not be piped into anything.
    /// </summary>
    private static int ListTags(Repository repo, string? sort, bool json, int annotateLines)
    {
        var tags = repo.Refs.ListTags();

        var sortedTags = sort?.TrimStart('-') switch
        {
            "creatordate" or "taggerdate" => (sort!.StartsWith('-')
                ? tags.OrderByDescending(t => TaggerDate(repo, t))
                : tags.OrderBy(t => TaggerDate(repo, t))).ToList(),
            "version:refname" => tags.OrderBy(t => t, VersionAwareComparer.Instance).ToList(),
            _ => (sort != null && sort.StartsWith('-')
                ? tags.OrderByDescending(t => t, StringComparer.Ordinal)
                : tags.OrderBy(t => t, StringComparer.Ordinal)).ToList()
        };

        if (json)
        {
            JsonOut.Write("tag", new
            {
                count = sortedTags.Count,
                tags = sortedTags.Select(t => new
                {
                    name = t,
                    target = repo.Refs.GetTag(t)?.ToHex(),
                    annotated = repo.Refs.GetTag(t) is { } h && repo.Objects.ReadObjectWithType(h)?.type == ObjectType.Tag
                }).ToList()
            });
            return 0;
        }

        foreach (string tag in sortedTags)
        {
            if (annotateLines <= 0)
            {
                Console.WriteLine(tag);
                continue;
            }

            // -n: append the first N lines of the annotation, indented, like a log listing.
            Hash? tagHash = repo.Refs.GetTag(tag);
            TagObject? tagObj = tagHash.HasValue ? repo.Objects.ReadTag(tagHash.Value) : null;
            if (tagObj == null)
            {
                Console.WriteLine(tag);
                continue;
            }

            var lines = tagObj.Message.TrimEnd('\n').Split('\n');
            Console.Write(tag.PadRight(20));
            Console.WriteLine(lines.Length > 0 ? lines[0] : "");
            for (int i = 1; i < Math.Min(annotateLines, lines.Length); i++)
                Console.WriteLine($"{new string(' ', 20)}{lines[i]}");
        }

        return 0;
    }

    /// <summary>
    /// The tagger date for sorting, or MinValue for a lightweight tag.
    ///
    /// A lightweight tag points straight at a commit, so it has no tagger and no tag object.
    /// Calling ReadTag on it throws, which crashed any date-ordered listing.
    /// </summary>
    private static DateTimeOffset TaggerDate(Repository repo, string tag)
    {
        Hash? h = repo.Refs.GetTag(tag);
        if (!h.HasValue) return DateTimeOffset.MinValue;
        if (repo.Objects.ReadObjectWithType(h.Value)?.type != ObjectType.Tag)
            return DateTimeOffset.MinValue;

        TagObject? tagObj = repo.Objects.ReadTag(h.Value);
        return tagObj?.Tagger.When ?? DateTimeOffset.MinValue;
    }

    /// <summary>Sorts "v1.10" after "v1.9" rather than lexically before it.</summary>
    private sealed class VersionAwareComparer : IComparer<string>
    {
        public static readonly VersionAwareComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x == null || y == null) return string.CompareOrdinal(x, y);
            string[] xs = x.Split('.', '-');
            string[] ys = y.Split('.', '-');
            for (int i = 0; i < Math.Max(xs.Length, ys.Length); i++)
            {
                string a = i < xs.Length ? xs[i] : "";
                string b = i < ys.Length ? ys[i] : "";
                bool aNum = int.TryParse(a, out int an);
                bool bNum = int.TryParse(b, out int bn);
                int cmp = aNum && bNum ? an.CompareTo(bn) : string.CompareOrdinal(a, b);
                if (cmp != 0) return cmp;
            }
            return 0;
        }
    }

    private static int CreateTag(Repository repo, string tagName, string? target,
        bool annotated, string? message, bool force)
    {
        Hash targetHash;
        if (target != null)
        {
            try
            {
                if (target == "HEAD")
                {
                    Hash? head = repo.Refs.GetHeadCommit();
                    if (!head.HasValue) throw new InvalidOperationException("HEAD does not exist");
                    targetHash = head.Value;
                }
                else if (Hash.TryParse(target) is Hash h)
                {
                    targetHash = h;
                }
                else if (target.Length >= 4)
                {
                    targetHash = Helpers.ResolvePartialHash(repo, target);
                }
                else
                {
                    Hash? branch = repo.Refs.GetBranch(target);
                    targetHash = branch!.Value;
                }
            }
            catch (Exception ex)
            {
                Terminal.WriteError($"fatal: {ex.Message}");
                return 1;
            }
        }
        else
        {
            Hash? head = repo.Refs.GetHeadCommit();
            if (!head.HasValue)
            {
                Terminal.WriteError("fatal: Failed to resolve 'HEAD' as a valid ref.");
                return 1;
            }
            targetHash = head.Value;
        }

        Hash? existingTag = repo.Refs.GetTag(tagName);
        if (existingTag.HasValue && !force)
        {
            Terminal.WriteError($"fatal: tag '{tagName}' already exists");
            return 1;
        }

        if (annotated)
        {
            if (message == null)
            {
                message = ReadTagMessage(tagName);
            }

            var tagObj = new TagObject
            {
                TargetHash = targetHash,
                TargetType = ObjectType.Commit,
                Name = tagName,
                Tagger = Signature.Now(repo.Config.GetUserName(), repo.Config.GetUserEmail()),
                Message = message ?? ""
            };

            Hash tagHash = repo.Objects.WriteTag(tagObj);
            repo.Refs.SetTag(tagName, tagHash, $"tag: {tagName}");
        }
        else
        {
            repo.Refs.SetTag(tagName, targetHash, $"tag: {tagName}");
        }

        Terminal.WriteSuccess($"Tag '{tagName}' created at {targetHash.Short}");
        return 0;
    }

    private static int DeleteTag(Repository repo, string tagName)
    {
        Hash? tagHash = repo.Refs.GetTag(tagName);
        if (tagHash == null)
        {
            Terminal.WriteError($"error: tag '{tagName}' not found.");
            return 1;
        }

        repo.Refs.DeleteTag(tagName);
        Terminal.WriteSuccess($"Deleted tag '{tagName}' (was {tagHash.Value.Short})");
        return 0;
    }

    private static string ReadTagMessage(string tagName)
    {
        string tmpFile = Path.Combine(Path.GetTempPath(), $"sm_tag_msg_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tmpFile,
                $"\n# Write a tag message for tag '{tagName}'\n" +
                "# Lines starting with '#' will be ignored.\n#\n");

            string? editor = Environment.GetEnvironmentVariable("SM_EDITOR")
                          ?? Environment.GetEnvironmentVariable("EDITOR")
                          ?? (Helpers.IsWindows ? "notepad.exe" : "vi");

            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = editor,
                Arguments = tmpFile,
                UseShellExecute = true
            });
            proc?.WaitForExit();

            string[] lines = File.ReadAllLines(tmpFile);
            var msgLines = lines.Where(l => !l.TrimStart().StartsWith('#')).ToList();
            return string.Join("\n", msgLines).Trim();
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }
}