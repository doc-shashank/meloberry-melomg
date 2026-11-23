using System.Globalization;
using System.Text.RegularExpressions;
using Melomg.Application.Abstractions;
using Melomg.Domain.Common;
using Melomg.Domain.Documents;
using Melomg.Domain.Errors;

namespace Melomg.Application.Addressing;

public enum AddressKind
{
    Document,
    FigureId,
    FigureNumber,
    FigureIndex,
    Page
}

public sealed record ParsedAddress(
    AddressKind Kind,
    string RawAddress,
    string? DocumentId,
    string? FigureId,
    string? RawNumber,
    int? IndexOrPage);

public sealed record ResolvedTarget(
    DocumentLookupContext DocumentContext,
    ParsedAddress Address,
    IReadOnlyList<DocumentFigure> Figures);

public sealed partial class AddressResolver(IDocumentWorkspaceRepository repository)
{
    public static Result<ParsedAddress> Parse(string? rawAddress)
    {
        if (string.IsNullOrWhiteSpace(rawAddress))
            return FigureErrors.InvalidAddress(string.Empty, "Address cannot be empty.");

        string addr = rawAddress.Trim();
        int hashIdx = addr.IndexOf('#');
        if (hashIdx < 0)
        {
            if (addr.StartsWith("Fig_", StringComparison.Ordinal))
                return new ParsedAddress(AddressKind.FigureId, addr, null, addr, null, null);

            if (addr.StartsWith("Doc_", StringComparison.Ordinal))
                return new ParsedAddress(AddressKind.Document, addr, addr, null, null, null);

            return FigureErrors.InvalidAddress(addr, "Expected Doc_<id>, Doc_<id>#<num|fN|pN>, or Fig_<id>.");
        }

        string docId = addr[..hashIdx].Trim();
        string fragment = addr[(hashIdx + 1)..].Trim();
        if (!docId.StartsWith("Doc_", StringComparison.Ordinal))
            return FigureErrors.InvalidAddress(addr, "Address prefix before '#' must be a Doc_<id>.");
        if (string.IsNullOrWhiteSpace(fragment))
            return FigureErrors.InvalidAddress(addr, "Fragment after '#' cannot be empty.");

        var idxMatch = FigureIndexFragmentRegex().Match(fragment);
        if (idxMatch.Success && int.TryParse(idxMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int figIdx))
            return new ParsedAddress(AddressKind.FigureIndex, addr, docId, null, null, figIdx);

        var pageMatch = PageFragmentRegex().Match(fragment);
        if (pageMatch.Success && int.TryParse(pageMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pageNum))
            return new ParsedAddress(AddressKind.Page, addr, docId, null, null, pageNum);

        string rawNum = fragment.StartsWith("n:", StringComparison.OrdinalIgnoreCase) && fragment.Length > 2
            ? fragment[2..].Trim()
            : fragment;

        if (string.IsNullOrWhiteSpace(rawNum))
            return FigureErrors.InvalidAddress(addr, "Figure number fragment cannot be empty.");

        return new ParsedAddress(AddressKind.FigureNumber, addr, docId, null, rawNum, null);
    }

    public async Task<Result<DocumentLookupContext>> ResolveDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var parsed = Parse(documentId);
        if (!parsed.IsSuccess) return parsed.Error!;
        if (parsed.Value.Kind != AddressKind.Document)
            return FigureErrors.InvalidAddress(documentId, "Expected a Doc_<id> without a figure selector.");

        var lookup = await repository.FindDocumentAsync(parsed.Value.DocumentId!, cancellationToken);
        return lookup is null ? DocumentErrors.NotFound(parsed.Value.DocumentId!) : lookup;
    }

    public async Task<Result<FigureOwnerContext>> ResolveSingleFigureAsync(string rawAddress, CancellationToken cancellationToken = default)
    {
        var parsedRes = Parse(rawAddress);
        if (!parsedRes.IsSuccess) return parsedRes.Error!;

        var parsed = parsedRes.Value;
        if (parsed.Kind == AddressKind.FigureId)
        {
            var owner = await repository.FindFigureOwnerAsync(parsed.FigureId!, cancellationToken);
            return owner is null ? FigureErrors.NotFound(parsed.FigureId!) : owner;
        }

        if (parsed.Kind is AddressKind.Document or AddressKind.Page)
            return FigureErrors.InvalidAddress(rawAddress, "Target must address a single figure (<docId>#<num>, <docId>#f<idx>, or <figId>).");

        var docLookup = await repository.FindDocumentAsync(parsed.DocumentId!, cancellationToken);
        if (docLookup is null) return DocumentErrors.NotFound(parsed.DocumentId!);

        var doc = docLookup.Document;
        if (parsed.Kind == AddressKind.FigureIndex)
        {
            int idx = parsed.IndexOrPage!.Value;
            if (idx < 1 || idx > doc.Figures.Count)
                return IndexErrors.OutOfIndexBounds(idx, 1, Math.Max(1, doc.Figures.Count));

            var byIdx = doc.FindFigureByIndex(idx);
            return byIdx is null
                ? FigureErrors.NotFound($"{doc.Id}#f{idx}")
                : new FigureOwnerContext(doc, byIdx, docLookup.StorageDirectory, docLookup.DatabasePath);
        }

        var (matches, isSubPanel) = doc.FindFiguresByNumber(parsed.RawNumber!);
        if (matches.Count == 0)
            return FigureErrors.NumberNotFound(parsed.RawNumber!, doc.Vocabulary, doc.Figures.Select(f => f.FigureNumber ?? string.Empty));

        if (matches.Count > 1 || isSubPanel)
        {
            var hints = isSubPanel
                ? matches.Select(m => $"{doc.Id}#{m.FigureNumber ?? $"f{m.Index}"}")
                : matches.Select(m => $"{doc.Id}#f{m.Index} (p{m.PageNumber})");
            return FigureErrors.AmbiguousNumber(parsed.RawNumber!, hints);
        }

        return new FigureOwnerContext(doc, matches[0], docLookup.StorageDirectory, docLookup.DatabasePath);
    }

    public async Task<Result<ResolvedTarget>> ResolveTargetAsync(string rawAddress, CancellationToken cancellationToken = default)
    {
        var parsedRes = Parse(rawAddress);
        if (!parsedRes.IsSuccess) return parsedRes.Error!;

        var parsed = parsedRes.Value;
        if (parsed.Kind == AddressKind.FigureId)
        {
            var owner = await repository.FindFigureOwnerAsync(parsed.FigureId!, cancellationToken);
            if (owner is null) return FigureErrors.NotFound(parsed.FigureId!);
            var ctx = new DocumentLookupContext(owner.Document, owner.StorageDirectory, owner.DatabasePath);
            return new ResolvedTarget(ctx, parsed, [owner.Figure]);
        }

        var docLookup = await repository.FindDocumentAsync(parsed.DocumentId!, cancellationToken);
        if (docLookup is null) return DocumentErrors.NotFound(parsed.DocumentId!);

        var doc = docLookup.Document;
        switch (parsed.Kind)
        {
            case AddressKind.Document:
                return new ResolvedTarget(docLookup, parsed, doc.Figures.ToList());

            case AddressKind.FigureIndex:
            {
                int idx = parsed.IndexOrPage!.Value;
                if (idx < 1 || idx > doc.Figures.Count)
                    return IndexErrors.OutOfIndexBounds(idx, 1, Math.Max(1, doc.Figures.Count));
                var byIdx = doc.FindFigureByIndex(idx);
                return byIdx is null
                    ? FigureErrors.NotFound($"{doc.Id}#f{idx}")
                    : new ResolvedTarget(docLookup, parsed, [byIdx]);
            }

            case AddressKind.Page:
            {
                int pageNum = parsed.IndexOrPage!.Value;
                if (doc.GetPage(pageNum) is null)
                    return DocumentErrors.InvalidPageNumber(pageNum, doc.PageCount);
                var pageFigs = doc.Figures.Where(f => f.PageNumber == pageNum).ToList();
                return pageFigs.Count > 0
                    ? new ResolvedTarget(docLookup, parsed, pageFigs)
                    : FigureErrors.NoFiguresMatch($"page {pageNum}");
            }

            case AddressKind.FigureNumber:
            {
                var (matches, isSubPanel) = doc.FindFiguresByNumber(parsed.RawNumber!);
                if (matches.Count == 0)
                    return FigureErrors.NumberNotFound(parsed.RawNumber!, doc.Vocabulary, doc.Figures.Select(f => f.FigureNumber ?? string.Empty));
                if (matches.Count > 1 && !isSubPanel)
                    return FigureErrors.AmbiguousNumber(parsed.RawNumber!, matches.Select(m => $"{doc.Id}#f{m.Index} (p{m.PageNumber})"));
                return new ResolvedTarget(docLookup, parsed, matches);
            }

            default:
                return FigureErrors.InvalidAddress(rawAddress, "Unsupported address form.");
        }
    }

    public static Result<IReadOnlyList<DocumentFigure>> ResolveSelectors(PdfDocument doc, IReadOnlyList<string> selectors)
    {
        var selected = new List<DocumentFigure>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        void AddUnique(DocumentFigure fig)
        {
            if (seenIds.Add(fig.Id))
                selected.Add(fig);
        }

        foreach (var rawToken in selectors)
        {
            if (string.IsNullOrWhiteSpace(rawToken)) continue;
            foreach (var part in rawToken.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string tok = part.Trim();
                if (tok.StartsWith('#'))
                    tok = tok[1..].Trim();

                if (string.Equals(tok, "all", StringComparison.OrdinalIgnoreCase))
                {
                    if (doc.Figures.Count == 0)
                        return FigureErrors.NoFiguresMatch("all");
                    foreach (var f in doc.Figures) AddUnique(f);
                    continue;
                }

                if (tok.StartsWith("Fig_", StringComparison.Ordinal))
                {
                    var byId = doc.FindFigure(tok);
                    if (byId is null) return FigureErrors.NotFound(tok);
                    AddUnique(byId);
                    continue;
                }

                var idxMatch = FigureIndexFragmentRegex().Match(tok);
                if (idxMatch.Success && int.TryParse(idxMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int figIdx))
                {
                    if (figIdx < 1 || figIdx > doc.Figures.Count)
                        return IndexErrors.OutOfIndexBounds(figIdx, 1, Math.Max(1, doc.Figures.Count));
                    var byIdx = doc.FindFigureByIndex(figIdx);
                    if (byIdx is null) return FigureErrors.NotFound($"{doc.Id}#f{figIdx}");
                    AddUnique(byIdx);
                    continue;
                }

                var pageMatch = PageFragmentRegex().Match(tok);
                if (pageMatch.Success && int.TryParse(pageMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pageNum))
                {
                    if (doc.GetPage(pageNum) is null)
                        return DocumentErrors.InvalidPageNumber(pageNum, doc.PageCount);
                    var pageFigs = doc.Figures.Where(f => f.PageNumber == pageNum).ToList();
                    if (pageFigs.Count == 0)
                        return FigureErrors.NoFiguresMatch($"page {pageNum}");
                    foreach (var pf in pageFigs) AddUnique(pf);
                    continue;
                }

                string rawNum = tok.StartsWith("n:", StringComparison.OrdinalIgnoreCase) && tok.Length > 2
                    ? tok[2..].Trim()
                    : tok;

                var (matches, isSubPanel) = doc.FindFiguresByNumber(rawNum);
                if (matches.Count == 0)
                {
                    // Legacy numeric index fallback ONLY if PDF has no figure matching that number AND token is a bare positive integer within range
                    if (int.TryParse(rawNum, NumberStyles.Integer, CultureInfo.InvariantCulture, out int legacyIdx) &&
                        legacyIdx >= 1 && legacyIdx <= doc.Figures.Count)
                    {
                        var fallback = doc.FindFigureByIndex(legacyIdx);
                        if (fallback is not null)
                        {
                            AddUnique(fallback);
                            continue;
                        }
                    }

                    return FigureErrors.NumberNotFound(rawNum, doc.Vocabulary, doc.Figures.Select(f => f.FigureNumber ?? string.Empty));
                }

                if (matches.Count > 1 && !isSubPanel)
                    return FigureErrors.AmbiguousNumber(rawNum, matches.Select(m => $"{doc.Id}#f{m.Index} (p{m.PageNumber})"));

                foreach (var m in matches) AddUnique(m);
            }
        }

        return selected.Count > 0 ? selected : FigureErrors.NoFiguresMatch(string.Join(",", selectors));
    }

    [GeneratedRegex(@"^f([1-9]\d*)$", RegexOptions.Compiled)]
    private static partial Regex FigureIndexFragmentRegex();

    [GeneratedRegex(@"^p([1-9]\d*)$", RegexOptions.Compiled)]
    private static partial Regex PageFragmentRegex();
}
