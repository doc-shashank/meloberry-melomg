namespace Melomg.Cli.Parsing;

public sealed record ParsedToken(string Value, bool WasQuoted);

public static class CommandTokenizer
{
    internal static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--out", "--app-data", "--model", "--provider", "--confidence", "--iou",
        "--title", "--view", "--page", "--category", "--min-conf",
        "--format", "--dpi", "--quality", "--padding",
        "--mask-match", "--mask-style", "--mask-color", "--naming",
        "--set", "--move", "--caption", "--label", "--num", "--index",
        "--match", "--style", "--color"
    };

    internal static readonly HashSet<string> BooleanFlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "silent", "json", "help",
        "project", "refresh", "retain-db", "offline",
        "include-caption", "mask-all", "no-masks", "meta", "keep-overlay", "manifest",
        "overlay", "skip-model", "keep-model", "force",
        "quiet", "verbose"
    };

    public static IReadOnlyList<ParsedToken> FromArgs(string[] args) =>
        args.Select(a => new ParsedToken(a, WasQuoted: false)).ToArray();
}
