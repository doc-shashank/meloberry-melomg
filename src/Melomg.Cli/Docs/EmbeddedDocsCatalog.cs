using System.Reflection;

namespace Melomg.Cli.Docs;

public sealed record EmbeddedDocTopic(
    string Topic,
    string FileName,
    string ResourceName,
    string Title,
    string Summary);

public static class EmbeddedDocsCatalog
{
    public static readonly IReadOnlyList<EmbeddedDocTopic> Topics =
    [
        new("overview", "README.md", "Melomg.Cli.Docs.README.md", "Overview & Quick Start", "Dual-boot REPL/Stateless modes, portable Doc_* SQLite workspaces, figure IDs, and exit codes."),
        new("commands", "COMMANDS.md", "Melomg.Cli.Docs.COMMANDS.md", "Canonical Command Reference", "Complete syntax, flags, examples, and invariants for every melomg verb."),
        new("onnx", "ONNX_AND_DETECTION.md", "Melomg.Cli.Docs.ONNX_AND_DETECTION.md", "ONNX Model & Hybrid PDF Pipeline", "Built-in melomg_doclayout_v1.onnx architecture, custom .onnx loading, NMS, and PNG/JPEG extraction."),
        new("agent", "AI_AGENT_PLAYBOOK.md", "Melomg.Cli.Docs.AI_AGENT_PLAYBOOK.md", "AI Agent Playbook", "Deterministic automation recipes, --json output contracts, fluent chains, and error recovery."),
        new("architecture", "ARCHITECTURE.md", "Melomg.Cli.Docs.ARCHITECTURE.md", "Clean CLI Architecture", "4-layer dependency rules, SQLite schema, and verification test suites.")
    ];

    public static EmbeddedDocTopic? FindTopic(string rawTopic)
    {
        var normalized = rawTopic.Trim();
        return Topics.FirstOrDefault(t =>
            string.Equals(t.Topic, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.FileName, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(t.FileName), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string ReadContent(EmbeddedDocTopic topic)
    {
        var assembly = typeof(EmbeddedDocsCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(topic.ResourceName)
            ?? throw new InvalidOperationException($"Embedded documentation resource '{topic.ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
