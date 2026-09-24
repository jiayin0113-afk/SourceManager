namespace SourceManager;

/// <summary>
/// Thrown when a revision expression cannot be resolved. Command handlers convert this into
/// a "fatal:" message and a non-zero exit code.
/// </summary>
public sealed class RevisionException : Exception
{
    public RevisionException(string message) : base(message) { }
}

/// <summary>
/// A resolved revision: the commit it names plus what it was named by.
/// </summary>
public readonly record struct RevisionTarget(Hash Commit, Hash? BranchHash, Hash? TagHash, string Display)
{
    public bool IsBranch => BranchHash.HasValue;
    public bool IsTag => TagHash.HasValue;
}

/// <summary>
/// Resolves revision expressions. One implementation is shared by every command so that
/// "which commits does this command accept" has a single answer instead of being re-invented
/// (and getting subtly different) in each command file.
///
/// Supported forms:
///   HEAD                      current commit
///   &lt;branch&gt;                  branch tip
///   &lt;tag&gt;                     tag target (annotated tags are peeled to their commit)
///   &lt;full-hex&gt; / &lt;partial-hex&gt;  object id, unambiguous abbreviations only
///   &lt;rev&gt;~&lt;n&gt; / &lt;rev&gt;~          n-th first-parent ancestor (n defaults to 1)
///   &lt;rev&gt;^&lt;n&gt; / &lt;rev&gt;^          n-th parent (n defaults to 1)
///   &lt;rev&gt;@{&lt;n&gt;}                n-th prior value from a ref's reflog (any ref, HEAD included)
///   &lt;rev&gt;@{&lt;time&gt;}             most recent value at or before a date ("yesterday", "2.hours.ago")
///   @{-&lt;n&gt;} / HEAD@{-&lt;n&gt;}       the branch checked out n checkouts ago
/// </summary>
public static class RevisionResolver
{
    public static RevisionTarget Resolve(Repository repo, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new RevisionException("empty revision");

        string spec = expression.Trim();
        string display = spec;

        // HEAD@{n} is consumed first because it may itself be followed by ancestry operators,
        // e.g. HEAD@{2}~1.
        Hash current = ResolveBase(repo, ref spec);

        while (spec.Length > 0)
        {
            char op = spec[0];
            if (op == '~')
            {
                spec = spec[1..];
                int n = ReadNumber(ref spec, 1);
                for (int i = 0; i < n; i++)
                {
                    Commit? c = repo.Objects.ReadCommit(current);
                    if (c == null)
                        throw new RevisionException($"revision '{display}' does not resolve to a commit");
                    if (c.ParentHashes.Count == 0)
                        throw new RevisionException($"revision '{display}' has no first-parent ancestor at depth {i + 1}");
                    current = c.ParentHashes[0];
                }
            }
            else if (op == '^')
            {
                spec = spec[1..];
                int n = ReadNumber(ref spec, 1);
                if (n == 0)
                {
                    // rev^0 means "the commit itself, peeling tags".
                    continue;
                }
                Commit? c = repo.Objects.ReadCommit(current);
                if (c == null)
                    throw new RevisionException($"revision '{display}' does not resolve to a commit");
                if (c.ParentHashes.Count < n)
                    throw new RevisionException($"revision '{display}' does not have parent {n}");
                current = c.ParentHashes[n - 1];
            }
            else
            {
                throw new RevisionException($"unrecognised revision syntax: '{expression}'");
            }
        }

        // Peeling: an annotated tag resolves to the commit it points at.
        current = PeelToCommit(repo, current);

        Hash? branchHash = repo.Refs.GetBranch(expression.Trim());
        Hash? tagHash = repo.Refs.GetTag(expression.Trim());
        return new RevisionTarget(current, branchHash, tagHash, display);
    }

    /// <summary>Resolves the leading atom and advances <paramref name="spec"/> past it.</summary>
    private static Hash ResolveBase(Repository repo, ref string spec)
    {
        // <base>@{<selector>} — a reflog entry of HEAD or of any named ref. Handled before the
        // ancestry scan because the base atom otherwise swallows the '@'.
        int at = spec.IndexOf("@{", StringComparison.Ordinal);
        if (at >= 0)
        {
            int close = spec.IndexOf('}', at + 2);
            if (close < 0)
                throw new RevisionException($"unterminated reflog selector: '{spec}'");
            string refPart = spec[..at];
            string inner = spec[(at + 2)..close];
            spec = spec[(close + 1)..];

            string display = string.IsNullOrEmpty(refPart) ? "HEAD" : refPart;
            string refName = string.IsNullOrEmpty(refPart) ? "HEAD" : ResolveReflogName(repo, refPart);
            return ResolveReflogSelector(repo, refName, inner, display);
        }

        // Longest-match base: the base atom runs until the first ancestry operator. Scanning
        // from the LEFT is what makes "HEAD~1" parse as atom "HEAD" plus "~1" — scanning from
        // the right treated the whole "HEAD~" as the atom and then found no operators.
        int cut = 0;
        while (cut < spec.Length && spec[cut] != '~' && spec[cut] != '^')
            cut++;
        string atom = spec[..cut];
        spec = spec[cut..];

        if (atom.Length == 0)
            throw new RevisionException("empty revision");

        if (atom == "HEAD")
            return CurrentCommit(repo) ?? throw new RevisionException("HEAD does not point to a commit");

        Hash? branch = repo.Refs.GetBranch(atom);
        if (branch.HasValue) return PeelToCommit(repo, branch.Value);

        Hash? tag = repo.Refs.GetTag(atom);
        if (tag.HasValue) return PeelToCommit(repo, tag.Value);

        Hash? remote = ResolveRemoteRef(repo, atom);
        if (remote.HasValue) return PeelToCommit(repo, remote.Value);

        if (LooksLikeHex(atom))
        {
            if (atom.Length == 64)
                return Hash.Parse(atom);
            if (atom.Length >= 4)
            {
                try { return Helpers.ResolvePartialHash(repo, atom); }
                catch (Exception ex) { throw new RevisionException(ex.Message); }
            }
        }

        throw new RevisionException($"unknown revision or path not in the working tree: '{atom}'");
    }

    /// <summary>Maps a base atom to the full ref name whose reflog should be consulted.</summary>
    private static string ResolveReflogName(Repository repo, string name)
    {
        if (name == "HEAD") return "HEAD";
        if (repo.Refs.GetBranch(name).HasValue) return $"refs/heads/{name}";
        if (repo.Refs.GetTag(name).HasValue) return $"refs/tags/{name}";

        int slash = name.IndexOf('/');
        if (slash > 0 && repo.Refs.GetRemoteBranch(name[..slash], name[(slash + 1)..]).HasValue)
            return $"refs/remotes/{name}";

        return $"refs/heads/{name}";
    }

    /// <summary>
    /// Resolves "@{...}" against a ref's reflog. Supports the numeric selector ("@{0}" is the most
    /// recent value), the branch selector ("@{-1}", the previous branch checked out), and date
    /// selectors ("@{yesterday}", "@{2.hours.ago}").
    /// </summary>
    private static Hash ResolveReflogSelector(Repository repo, string refName, string inner, string display)
    {
        var entries = repo.Refs.GetReflog(refName, 0); // newest-first, all
        if (entries.Count == 0)
            throw new RevisionException($"reflog for '{display}' is empty");

        // @{-N}: the N-th branch HEAD was on before the current one.
        if (inner.StartsWith('-'))
        {
            if (!int.TryParse(inner, out int back) || back >= 0)
                throw new RevisionException($"invalid reflog selector '{display}@{{{inner}}}'");
            Hash? previous = FindPreviousCheckout(repo, -back);
            if (!previous.HasValue || previous.Value.Equals(Hash.Zero))
                throw new RevisionException($"no previous checkout for '{display}@{{{inner}}}'");
            return previous.Value;
        }

        if (int.TryParse(inner, out int index))
        {
            if (index < 0)
                throw new RevisionException($"invalid reflog selector '{display}@{{{inner}}}'");
            if (index >= entries.Count)
                throw new RevisionException($"{display} reflog has only {entries.Count} entries; '{display}@{{{index}}}' is out of range");
            return entries[index].NewHash;
        }

        if (DateSpec.TryParse(inner, out DateTimeOffset cutoff))
        {
            foreach (var entry in entries)
                if (entry.Timestamp <= cutoff) return entry.NewHash;
            throw new RevisionException($"no reflog entry for '{display}' at or before '{inner}'");
        }

        throw new RevisionException($"invalid reflog selector '{display}@{{{inner}}}'");
    }

    /// <summary>The commit HEAD pointed at before the <paramref name="n"/>-th most recent checkout.</summary>
    private static Hash? FindPreviousCheckout(Repository repo, int n)
    {
        var entries = repo.Refs.GetReflog("HEAD", 0); // newest-first
        int seen = 0;
        foreach (var entry in entries)
        {
            if (entry.Message.StartsWith("checkout: moving from", StringComparison.Ordinal))
            {
                seen++;
                if (seen == n) return entry.OldHash;
            }
        }
        return null;
    }

    private static Hash? CurrentCommit(Repository repo)
    {
        Hash? head = repo.Refs.GetHeadCommit();
        if (!head.HasValue) return null;
        // An unborn branch stores a zero hash; treat that as "no commit".
        if (head.Value.Equals(Hash.Zero)) return null;
        return head.Value;
    }

    private static Hash? ResolveRemoteRef(Repository repo, string atom)
    {
        int slash = atom.IndexOf('/');
        if (slash <= 0) return null;
        return repo.Refs.GetRemoteBranch(atom[..slash], atom[(slash + 1)..]);
    }

    /// <summary>Follows tag objects down to the commit they ultimately reference.</summary>
    public static Hash PeelToCommit(Repository repo, Hash hash)
    {
        var seen = new HashSet<Hash>();
        Hash current = hash;
        while (seen.Add(current))
        {
            if (repo.Objects.ReadCommit(current) != null) return current;
            if (repo.Objects.ReadObjectWithType(current)?.type != ObjectType.Tag) return current;
            TagObject? tag = repo.Objects.ReadTag(current);
            if (tag == null) return current;
            current = tag.TargetHash;
        }
        return current;
    }

    private static int ReadNumber(ref string spec, int fallback)
    {
        int i = 0;
        while (i < spec.Length && char.IsAsciiDigit(spec[i])) i++;
        if (i == 0) return fallback;
        int value = int.Parse(spec[..i]);
        spec = spec[i..];
        return value;
    }

    private static bool LooksLikeHex(string s)
    {
        if (s.Length < 4 || s.Length > 64) return false;
        foreach (char c in s)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    /// <summary>
    /// Resolves "&lt;rev&gt;:&lt;path&gt;" — the path of an object inside a revision's tree.
    /// Returns the blob (or subtree) id, which is what `show HEAD~1:file` and `cat-file` need.
    /// </summary>
    public static bool TryResolveRevisionPath(Repository repo, string expression, out Hash objectId, out string path)
    {
        objectId = Hash.Zero;
        path = "";

        int colon = expression.IndexOf(':');
        if (colon <= 0) return false;

        string revSpec = expression[..colon];
        string pathSpec = expression[(colon + 1)..].TrimStart('/');
        if (pathSpec.Length == 0) return false;

        if (!TryResolve(repo, revSpec, out RevisionTarget revision)) return false;

        Commit? commit = repo.Objects.ReadCommit(revision.Commit);
        if (commit?.TreeHash.HasValue != true) return false;

        var files = repo.GetTreeEntries(commit.TreeHash.Value);
        if (!files.TryGetValue(pathSpec, out Hash blob)) return false;

        objectId = blob;
        path = pathSpec;
        return true;
    }

    /// <summary>
    /// Resolves a user-supplied value that may be a revision or a repository path.
    /// </summary>
    public static bool TryResolve(Repository repo, string expression, out RevisionTarget target)
    {
        try
        {
            target = Resolve(repo, expression);
            return true;
        }
        catch (RevisionException)
        {
            target = default;
            return false;
        }
    }
}
