namespace SourceManager;

/// <summary>
/// Shared date-spec parsing for --since / --until / --prune style arguments.
///
/// Relative forms ("2.weeks.ago", "3.days.ago", "now") and absolute dates are both accepted.
/// Keeping this in one place means every command interprets the same string identically.
/// </summary>
public static class DateSpec
{
    /// <summary>Parses a spec into an absolute cutoff.</summary>
    public static bool TryParse(string spec, out DateTimeOffset cutoff)
        => TryParseSince(spec, out cutoff, out _);

    /// <summary>
    /// Parses a spec, also reporting the relative span when one was given (which is what a
    /// grace-period option needs: not "before Tuesday" but "older than 14 days").
    /// </summary>
    public static bool TryParseSince(string spec, out DateTimeOffset cutoff, out TimeSpan delta)
    {
        cutoff = DateTimeOffset.MinValue;
        delta = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(spec)) return false;

        string s = spec.Trim();
        if (s.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            cutoff = DateTimeOffset.Now;
            return true;
        }

        if (s.EndsWith(".ago", StringComparison.OrdinalIgnoreCase))
        {
            string body = s[..^4];
            string[] parts = body.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int n))
            {
                TimeSpan? amount = parts[1].TrimEnd('s').ToLowerInvariant() switch
                {
                    "second" => TimeSpan.FromSeconds(n),
                    "minute" => TimeSpan.FromMinutes(n),
                    "hour" => TimeSpan.FromHours(n),
                    "day" => TimeSpan.FromDays(n),
                    "week" => TimeSpan.FromDays(n * 7),
                    "month" => TimeSpan.FromDays(n * 30),
                    "year" => TimeSpan.FromDays(n * 365),
                    _ => null
                };

                if (amount.HasValue)
                {
                    delta = amount.Value;
                    cutoff = DateTimeOffset.Now - amount.Value;
                    return true;
                }
            }
        }

        if (DateTimeOffset.TryParse(s, out cutoff))
        {
            delta = DateTimeOffset.Now - cutoff;
            return true;
        }

        return false;
    }
}
