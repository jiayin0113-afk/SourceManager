namespace SourceManager;

/// <summary>
/// `sm verify` — proves the repository is internally consistent.
///
/// Content addressing means every object's identity is derived from its bytes, so integrity is
/// not a matter of trust: it can be recomputed. This command checks that every object hashes to
/// its own name, that every reference inside every object resolves, that refs and the index point
/// at real objects, and (optionally) that nothing has become unreachable.
/// </summary>
public static class VerifyCmd
{
    public static int Execute(ParseResult args)
    {
        var repo = Repository.FindRequired();
        bool json = JsonOut.Requested(args);
        bool checkUnreachable = args.GetBoolOption("unreachable") || args.GetBoolOption("full");
        bool quiet = args.GetBoolOption("quiet");

        var report = VerifyEngine.Run(repo, checkUnreachable);

        if (json)
        {
            JsonOut.Write("verify", new
            {
                ok = report.Ok,
                objects = new
                {
                    total = report.ObjectCount,
                    commits = report.CommitCount,
                    trees = report.TreeCount,
                    blobs = report.BlobCount,
                    tags = report.TagCount,
                    bytesOnDisk = report.BytesOnDisk
                },
                errors = report.ErrorCount,
                warnings = report.WarningCount,
                findings = report.Findings.Select(f => new
                {
                    severity = f.Severity,
                    code = f.Code,
                    message = f.Message,
                    objectId = f.Path
                }).ToList()
            });
            return report.Ok ? 0 : 1;
        }

        if (!quiet)
        {
            Terminal.WriteLine(
                $"Checked {report.ObjectCount} object(s): {report.CommitCount} commit(s), " +
                $"{report.TreeCount} tree(s), {report.BlobCount} blob(s), {report.TagCount} tag(s), " +
                $"{Helpers.FormatFileSize(report.BytesOnDisk)} on disk.",
                Terminal.Color.BrightCyan);
        }

        foreach (var finding in report.Findings)
        {
            if (finding.Severity == "error")
                Terminal.WriteError($"error: {finding.Message}");
            else if (!quiet)
                Terminal.WriteWarning(finding.Message);
        }

        if (report.Ok)
        {
            if (!quiet)
                Terminal.WriteSuccess(report.WarningCount == 0
                    ? "Repository verified: every object matches its id and every reference resolves."
                    : $"Repository verified with {report.WarningCount} warning(s).");
            return 0;
        }

        Terminal.WriteError($"Repository FAILED verification: {report.ErrorCount} error(s), {report.WarningCount} warning(s).");
        return 1;
    }
}
