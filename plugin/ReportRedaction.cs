using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// No Unity or environment reads: export and archive verification use the same captured identity.
// Text may be raw (logs, XML) or JSON (journals, report.json): MiniJSON writes chars outside 32..126 as \uXXXX and
// backslashes as \\, so every rule also matches escaped forms and never matches the letter of an escape sequence.
internal sealed class ReportRedaction
{
    // Common account words stay readable in prose (bare words), but are always removed from path segments.
    internal static readonly string[] Stoplist = { "user", "admin", "deck", "steamuser", "public", "system", "player", "machine" };
    private const RegexOptions Ignore = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Escaped = @"\\u[0-9A-Fa-f]{4}";
    // A separator never steals the backslash that starts a \uXXXX escape.
    private const string Sep = @"(?:/|\\(?!u[0-9A-Fa-f]{4}))+";
    private const string Segment = "(?:" + Escaped + @"|[^\\/\r\n\t\x22<>:,;|?*])+";
    private const string SegmentNoSpace = "(?:" + Escaped + @"|[^\\/\s\x22<>:,;|?*])+";
    private enum Kind { Any, PathSegment, Bare }
    private sealed class Rule { internal Regex Pattern; internal string Replacement; internal Kind Kind; internal int PathLength = -1; }
    private int pathRules;
    private sealed class Identity { internal string Name, Label; internal bool Bare, PathSegment; }
    private readonly List<Rule> rules = new List<Rule>();
    private readonly List<Identity> identities = new List<Identity>();
    internal int Identities => identities.Count(identity => identity.Bare || identity.PathSegment);

    internal ReportRedaction(string user, string profile, string localAppData, string machine, params string[] otherUsers)
    {
        foreach (var path in new[] { localAppData, profile }.Where(value => !string.IsNullOrEmpty(value)).OrderByDescending(value => value.Length))
            Add(PathPattern(path), path == localAppData ? "<localappdata>" : "<userprofile>", Kind.Any);
        // Any Users segment after a separator or a drive/admin share (C:, C$), keeping the separator style.
        Add(@"(?<=[\\/]|\b[A-Za-z][:$])(Users)(" + Sep + ")(?!<)" + Segment, "$1$2<user>", Kind.Any);
        Add(@"(?<=[\\/])(home)(" + Sep + ")(?!<)" + SegmentNoSpace, "$1$2<user>", Kind.Any);
        Add(@"(?<=[\\/])(run" + Sep + "media)(" + Sep + ")(?!<)" + SegmentNoSpace, "$1$2<user>", Kind.Any);
        Add(@"(?<=[\\/])Battle(?:\.)?net(?=[\\/\s\x22]|$)", "<battlenet>", Kind.Any);
        // USERPROFILE's folder can differ from UserName (8.3 EXAMPL~1, renamed accounts); Wine/Proton adds the Linux user.
        var users = new[] { user, LastSegment(profile) }.Concat(otherUsers ?? new string[0])
            .Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < users.Count; i++)
        {
            var identity = new Identity { Name = users[i], Label = "user" + i, PathSegment = users[i].Length >= 3, Bare = Eligible(users[i]) };
            identities.Add(identity);
            if (identity.PathSegment) Add(NamePattern(users[i]), "<user>", Kind.PathSegment);
        }
        foreach (var identity in identities.Where(identity => identity.Bare).ToList()) Add(NamePattern(identity.Name), "<user>", Kind.Bare);
        if (!string.IsNullOrEmpty(machine))
        {
            identities.Add(new Identity { Name = machine, Label = "machine", Bare = Eligible(machine) });
            if (Eligible(machine)) Add(NamePattern(machine), "<machine>", Kind.Bare);
        }
    }
    // A folder that holds personal names but is not under the profile: the install root, the saves folder, the retail folder.
    // These run before every other rule and longest first, so "D:\Games\Ann\SoloStone\client" becomes "<solostone>\client".
    internal void AddPath(string path, string placeholder)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim('\\', '/').Length < 3) return;
        var rule = new Rule { Pattern = new Regex(PathPattern(path, @"[\\/\s\x22.,;:)\]'>]"), Ignore), Replacement = placeholder, Kind = Kind.Any, PathLength = path.TrimEnd('\\', '/').Length };
        int at = 0;
        while (at < pathRules && rules[at].PathLength >= rule.PathLength) at++;
        rules.Insert(at, rule); pathRules++;
    }
    // Environment snapshot (pure: the caller passes the reader). WINEHOMEDIR is "\??\Z:\home\<linux user>".
    internal static ReportRedaction FromEnvironment(string userName, string machineName, string localAppData, Func<string, string> environment)
    {
        return new ReportRedaction(userName, environment("USERPROFILE"), localAppData, machineName,
            environment("WINEUSERNAME"), LastSegment(StripNtPrefix(environment("WINEHOMEDIR"))));
    }
    internal static string StripNtPrefix(string path) => path != null && path.StartsWith(@"\??\", StringComparison.Ordinal) ? path.Substring(4) : path;
    internal static string LastSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string trimmed = path.TrimEnd('\\', '/');
        int slash = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        string last = slash < 0 ? trimmed : trimmed.Substring(slash + 1);
        return last.Length == 0 || last.EndsWith(":", StringComparison.Ordinal) ? null : last;
    }
    private static bool Eligible(string name) => name.Length >= 3 && !Stoplist.Contains(name.ToLowerInvariant());
    private static string Hex(char c) => ((int)c).ToString("x4", CultureInfo.InvariantCulture);
    private static bool Plain(char c) => c >= 32 && c <= 126;
    // Each char outside 32..126 also matches its \uXXXX form in either letter case.
    private static string NamePattern(string name) => string.Concat(name.Select(c => Plain(c) ? Regex.Escape(c.ToString())
        : "(?:" + Regex.Escape(c.ToString()) + @"|\\u" + Hex(char.ToUpperInvariant(c)) + @"|\\u" + Hex(char.ToLowerInvariant(c)) + ")"));
    private static string PathPattern(string path, string end = @"[\\/\s\x22]") => string.Join(Sep, Regex.Split(path.TrimEnd('\\', '/'), @"[\\/]+").Select(NamePattern))
        + "(?=" + end + "|$)(?!" + Escaped + ")";
    private void Add(string pattern, string replacement, Kind kind) => rules.Add(new Rule { Pattern = new Regex(pattern, Ignore), Replacement = replacement, Kind = kind });

    internal string Redact(string text, bool json = false)
    {
        text = text ?? "";
        foreach (var rule in rules)
        {
            StringBuilder output = null; int last = 0;
            for (Match match = rule.Pattern.Match(text); match.Success;)
            {
                if (match.Length > 0 && Accept(text, match.Index, match.Length, json, rule.Kind))
                {
                    (output = output ?? new StringBuilder(text.Length)).Append(text, last, match.Index - last).Append(match.Result(rule.Replacement));
                    last = match.Index + match.Length; match = rule.Pattern.Match(text, last);
                }
                else match = match.Index + 1 <= text.Length ? rule.Pattern.Match(text, match.Index + 1) : Match.Empty;
            }
            if (output != null) text = output.Append(text, last, text.Length - last).ToString();
        }
        return text;
    }
    // Rule kinds still matching (would be redacted again). Only kinds leave the checker; it never echoes matched text.
    internal List<string> Check(string text, bool json = false)
    {
        var hits = new List<string>(); text = text ?? "";
        for (int i = 0; i < rules.Count; i++)
            for (Match match = rules[i].Pattern.Match(text); match.Success; match = match.Index + 1 <= text.Length ? rules[i].Pattern.Match(text, match.Index + 1) : Match.Empty)
                if (match.Length > 0 && Accept(text, match.Index, match.Length, json, rules[i].Kind)) { hits.Add("rule-" + i); break; }
        return hits;
    }
    // Independent verifier: literal (case-insensitive) search for each raw identity and its \uXXXX-escaped forms,
    // not the redaction regexes. Same min-length/stoplist semantics. Returns "identity-<label>x<count>" kinds only.
    internal List<string> Leaks(string text, bool json = false)
    {
        var hits = new List<string>(); text = text ?? "";
        foreach (var identity in identities.Where(identity => identity.Bare || identity.PathSegment))
        {
            int count = 0;
            foreach (string form in Forms(identity.Name))
                for (int at = text.IndexOf(form, StringComparison.OrdinalIgnoreCase); at >= 0; at = at + 1 < text.Length ? text.IndexOf(form, at + 1, StringComparison.OrdinalIgnoreCase) : -1)
                    if ((identity.Bare && Accept(text, at, form.Length, json, Kind.Bare)) || (identity.PathSegment && Accept(text, at, form.Length, json, Kind.PathSegment))) count++;
            if (count > 0) hits.Add("identity-" + identity.Label + "x" + count);
        }
        return hits;
    }
    private static IEnumerable<string> Forms(string name)
    {
        var forms = new List<string> { name };
        if (name.Any(c => !Plain(c)))
            foreach (string variant in new[] { name, name.ToUpperInvariant(), name.ToLowerInvariant() })
                forms.Add(string.Concat(variant.Select(c => Plain(c) ? c.ToString() : @"\u" + Hex(c))));
        return forms.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // Shared occurrence semantics for the redactor and the verifier.
    private static bool Accept(string text, int index, int length, bool json, Kind kind)
    {
        // JSON: the letter of an escape sequence (\n, \t, \u...) is never a name or path start.
        if (json && OddBackslashes(text, index) && "bfnrtu\"/\\".IndexOf(text[index]) >= 0) return false;
        int end = index + length;
        if (kind == Kind.PathSegment)
            return index > 0 && (text[index - 1] == '/' || text[index - 1] == '\\')
                && (end == text.Length || ("/\\\"".IndexOf(text[end]) >= 0 || char.IsWhiteSpace(text[end])) && !EscapeAt(text, end));
        if (kind == Kind.Bare)
            return !PreviousIsWord(text, index, json) && !NextIsWord(text, end) && !JsonKey(text, index, end);
        return true;
    }
    private static bool OddBackslashes(string text, int index)
    {
        int count = 0; while (index - count - 1 >= 0 && text[index - count - 1] == '\\') count++;
        return count % 2 == 1;
    }
    private static bool EscapeAt(string text, int index) => index >= 0 && index + 5 < text.Length && text[index] == '\\' && text[index + 1] == 'u'
        && IsHex(text[index + 2]) && IsHex(text[index + 3]) && IsHex(text[index + 4]) && IsHex(text[index + 5]);
    private static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    private static bool Word(char c)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        return char.IsLetter(c) || category == UnicodeCategory.DecimalDigitNumber || category == UnicodeCategory.NonSpacingMark
            || category == UnicodeCategory.ConnectorPunctuation || c == '<' || c == '>';
    }
    private static bool PreviousIsWord(string text, int index, bool json)
    {
        if (index == 0) return false;
        if (index >= 6 && EscapeAt(text, index - 6) && !OddBackslashes(text, index - 6)) return true; // escaped letter
        char c = text[index - 1];
        if (json && "bfnrt".IndexOf(c) >= 0 && OddBackslashes(text, index - 1)) return false; // \n etc. end a word
        return Word(c);
    }
    private static bool NextIsWord(string text, int end) => end < text.Length && (EscapeAt(text, end) || Word(text[end]));
    // A bare word that is a whole JSON object key ("name": ...) is structure, not data.
    private static bool JsonKey(string text, int index, int end)
    {
        if (index == 0 || text[index - 1] != '"' || OddBackslashes(text, index - 1) || end >= text.Length || text[end] != '"') return false;
        int next = end + 1; while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
        return next < text.Length && text[next] == ':';
    }
}
