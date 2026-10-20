using System.Text.RegularExpressions;

namespace Leaf.Plugins.LeafCommons;

public sealed record SecretHit(string Rule, int Line);

public sealed class SecretScanner
{
    private static readonly Regex NamedSecretAssignment = new(
        """(?im)\b(?<key>api[_-]?key|access[_-]?token|auth[_-]?token|token|client[_-]?secret|password)\b\s*[:=]\s*(?<value>"[^"\r\n]+"|'[^'\r\n]+'|[^\s,;)\]}]+)""",
        RegexOptions.Compiled);
    private static readonly (string Name, Regex Pattern)[] Rules =
    [
        ("private-key", new(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("jwt", new(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.Compiled)),
        ("provider-key", new(@"\b(?:AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|sk-(?:proj-)?[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("connection-string", new(@"(?i)\b(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|redis):\/\/[^\s]+", RegexOptions.Compiled))
    ];

    public IReadOnlyList<SecretHit> Scan(string text)
    {
        var hits = new List<SecretHit>();
        foreach (var (name, pattern) in Rules)
            foreach (Match match in pattern.Matches(text))
                hits.Add(new(name, 1 + text.AsSpan(0, match.Index).Count('\n')));
        foreach (Match match in NamedSecretAssignment.Matches(text))
            if (LooksLikeAssignedSecret(match.Groups["key"].Value, match.Groups["value"].Value))
                hits.Add(new("named-secret", 1 + text.AsSpan(0, match.Index).Count('\n')));
        var pathStripped = Regex.Replace(text, @"\b[A-Za-z]:[\\/][^\s\""'<>]+|(?:^|\s)/(?:[A-Za-z0-9._-]+/)+[A-Za-z0-9._-]+", "");
        foreach (Match match in Regex.Matches(pathStripped, @"\b[A-Za-z0-9+/_=-]{32,}\b"))
        {
            var value = match.Value.TrimEnd('=');
            if (Regex.IsMatch(value, "^[a-f0-9]+$", RegexOptions.IgnoreCase)) continue;
            if (!value.Any(char.IsLower) || !value.Any(char.IsUpper) || !value.Any(char.IsDigit) || value.Distinct().Count() < 12) continue;
            var entropy = value.GroupBy(c => c).Select(g => (double)g.Count() / value.Length).Sum(p => -p * Math.Log2(p));
            if (entropy >= 3.7) hits.Add(new("high-entropy", 1 + text.AsSpan(0, Math.Min(match.Index, text.Length)).Count('\n')));
        }
        return hits;
    }

    private static bool LooksLikeAssignedSecret(string key, string rawValue)
    {
        var quoted = rawValue.Length >= 2
            && rawValue[0] is '\'' or '"'
            && rawValue[^1] == rawValue[0];
        var value = quoted ? rawValue[1..^1] : rawValue;
        if (value.Length < 8 || IsPlaceholder(value)) return false;
        if (!quoted && Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_.]*\(")) return false;

        var strongKey = key.Contains("key", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Equals("password", StringComparison.OrdinalIgnoreCase);
        if (quoted && strongKey) return true;

        var hasUpper = value.Any(char.IsUpper);
        var hasLower = value.Any(char.IsLower);
        var hasDigit = value.Any(char.IsDigit);
        var hasSymbol = value.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '_' and not '-');
        var varied = value.Distinct().Count() >= 8;
        return value.Length >= 16 && varied
            && ((hasDigit && (hasUpper || hasSymbol))
                || (quoted && hasUpper && hasLower && value.Length >= 20));
    }

    private static bool IsPlaceholder(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.StartsWith("${", StringComparison.Ordinal)
            || normalized.StartsWith("{{", StringComparison.Ordinal)
            || normalized.StartsWith('<')
            || normalized.Contains("placeholder", StringComparison.Ordinal)
            || normalized.Contains("redacted", StringComparison.Ordinal)
            || normalized.Contains("example", StringComparison.Ordinal)
            || normalized.StartsWith("your_", StringComparison.Ordinal)
            || normalized.StartsWith("your-", StringComparison.Ordinal);
    }
}

public sealed record FilterDecision(bool Allowed, string? Rule = null, bool Alarm = false);

public sealed class OutboundFilter(SecretScanner secrets)
{
    private static readonly Regex WindowsAbsolute = new("""(?i)(?:^|[\s"'(<])(?:[A-Z]:\\|\\\\)[^\s"'>)]*""", RegexOptions.Compiled);
    private static readonly Regex PosixAbsolute = new("""(?:^|[\s"'(<])/(?:Users|home|root|etc|var|opt|private|mnt|srv)/[^\s"'>)]*""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GeneralPosixPath = new(@"(?:^|\s)(/[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)+)", RegexOptions.Compiled);
    private static readonly Regex Profile = new(@"(?i)(?:C:\\Users\\[^\\\s]+|/Users/[^/\s]+|/home/[^/\s]+)(?:[/\\][^\s]*)?", RegexOptions.Compiled);

    public FilterDecision Check(string? text, IEnumerable<string> personalWords, IEnumerable<string> canaries)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(false, "empty");
        if (text.Length > 2000) return new(false, "content-too-long");
        foreach (var canary in canaries.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (text.Contains(canary, StringComparison.Ordinal)) return new(false, "canary", true);
        foreach (var word in personalWords.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (Regex.IsMatch(text, $@"(?i)(?<![\p{{L}}\p{{N}}_]){Regex.Escape(word.Trim())}(?![\p{{L}}\p{{N}}_])"))
                return new(false, "personal-word");
        if (Profile.IsMatch(text)) return new(false, "user-profile-path");
        if (WindowsAbsolute.IsMatch(text) || PosixAbsolute.IsMatch(text)) return new(false, "absolute-path");
        foreach (Match path in GeneralPosixPath.Matches(text))
            if (!path.Groups[1].Value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !path.Groups[1].Value.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)) return new(false, "absolute-path");
        var hit = secrets.Scan(text).FirstOrDefault();
        return hit is null ? new(true) : new(false, $"secret:{hit.Rule}");
    }
}
