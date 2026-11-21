using System.Text;

namespace Melomg.Cli.Parsing;

public sealed record ParsedToken(string Value, bool WasQuoted);

public static class CommandTokenizer
{
    internal static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--doc", "--figure", "--model", "--app-data", "--provider", "--confidence", "--iou",
        "--title", "--page", "--category", "--min-confidence", "--caption", "--label",
        "--format", "--dpi", "--quality", "--padding", "--obfuscate-label",
        "--obfuscate-style", "--obfuscate-color", "--match", "--style", "--color"
    };

    internal static readonly HashSet<string> BooleanFlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "include-caption", "obfuscate-labels", "no-masks", "meta",
        "keep-overlay", "retain-db", "overlay", "offline", "skip-model", "keep-model", "force"
    };

    internal const string UnclosedQuoteSentinel = "\0UNCLOSED_QUOTE";

    public static IReadOnlyList<IReadOnlyList<ParsedToken>> SplitIntoChainStages(string rawLine)
    {
        var tokens = TokenizeLine(rawLine);
        return SplitTokenStages(tokens);
    }

    public static IReadOnlyList<IReadOnlyList<ParsedToken>> SplitArgsIntoChainStages(string[] args)
    {
        if (args.Length == 1 && args[0].Contains("->", StringComparison.Ordinal))
            return SplitIntoChainStages(args[0]);

        int nonFlagChainIndex = -1;
        int nonFlagCount = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) &&
                !args[i].Contains("->", StringComparison.Ordinal) &&
                !args[i].Any(char.IsWhiteSpace))
            {
                if (i + 1 < args.Length && ValueOptions.Contains(args[i]))
                    i++;
                continue;
            }
            nonFlagCount++;
            nonFlagChainIndex = i;
        }

        if (nonFlagCount == 1 && nonFlagChainIndex >= 0 && args[nonFlagChainIndex].Contains("->", StringComparison.Ordinal))
        {
            var stagesFromQuoted = SplitIntoChainStages(args[nonFlagChainIndex]).Select(s => s.ToList()).ToList();
            if (stagesFromQuoted.Count > 0)
            {
                var leadingFlags = new List<ParsedToken>();
                for (int i = 0; i < args.Length; i++)
                {
                    if (i == nonFlagChainIndex)
                        continue;
                    leadingFlags.Add(new ParsedToken(args[i], WasQuoted: false));
                }
                stagesFromQuoted[0].InsertRange(0, leadingFlags);
            }
            return stagesFromQuoted;
        }

        var stages = new List<List<ParsedToken>>();
        var current = new List<ParsedToken>();
        foreach (var arg in args)
        {
            if (arg == "->")
            {
                if (current.Count > 0)
                {
                    stages.Add(current);
                    current = [];
                }
            }
            else
            {
                current.Add(new ParsedToken(arg, WasQuoted: false));
            }
        }

        if (current.Count > 0)
            stages.Add(current);

        return stages;
    }

    public static IReadOnlyList<ParsedToken> TokenizeLine(string rawLine)
    {
        var tokens = new List<ParsedToken>();
        if (string.IsNullOrWhiteSpace(rawLine))
            return tokens;

        var sb = new StringBuilder();
        bool inDouble = false;
        bool inSingle = false;
        bool tokenQuoted = false;
        bool hasToken = false;

        for (int i = 0; i < rawLine.Length; i++)
        {
            char c = rawLine[i];

            if (inDouble)
            {
                if (c == '\\' && i + 1 < rawLine.Length && (rawLine[i + 1] == '"' || rawLine[i + 1] == '\\'))
                {
                    sb.Append(rawLine[++i]);
                }
                else if (c == '"')
                {
                    inDouble = false;
                }
                else
                {
                    sb.Append(c);
                }
                continue;
            }

            if (inSingle)
            {
                if (c == '\'')
                    inSingle = false;
                else
                    sb.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(new ParsedToken(sb.ToString(), tokenQuoted));
                    sb.Clear();
                    hasToken = false;
                    tokenQuoted = false;
                }
                continue;
            }

            if (c == '"')
            {
                inDouble = true;
                tokenQuoted = true;
                hasToken = true;
                continue;
            }

            if (c == '\'')
            {
                inSingle = true;
                tokenQuoted = true;
                hasToken = true;
                continue;
            }

            if (!hasToken && c == '-' && i + 1 < rawLine.Length && rawLine[i + 1] == '>' &&
                (i + 2 >= rawLine.Length || char.IsWhiteSpace(rawLine[i + 2])))
            {
                tokens.Add(new ParsedToken("->", WasQuoted: false));
                i++;
                continue;
            }

            hasToken = true;
            sb.Append(c);
        }

        if (hasToken)
            tokens.Add(new ParsedToken(sb.ToString(), tokenQuoted));

        if (inDouble || inSingle)
            tokens.Add(new ParsedToken(UnclosedQuoteSentinel, WasQuoted: false));

        return tokens;
    }

    private static IReadOnlyList<IReadOnlyList<ParsedToken>> SplitTokenStages(IReadOnlyList<ParsedToken> tokens)
    {
        var stages = new List<List<ParsedToken>>();
        var current = new List<ParsedToken>();

        foreach (var token in tokens)
        {
            if (!token.WasQuoted && token.Value == "->")
            {
                if (current.Count > 0)
                {
                    stages.Add(current);
                    current = [];
                }
            }
            else
            {
                current.Add(token);
            }
        }

        if (current.Count > 0)
            stages.Add(current);

        return stages;
    }
}
