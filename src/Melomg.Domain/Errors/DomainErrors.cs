using Melomg.Domain.Common;
using Melomg.Domain.Documents;

namespace Melomg.Domain.Errors;

public static class IndexErrors
{
    public static Error OutOfIndexBounds(int index, int min, int max) =>
        new("Index.OutOfIndexBounds", $"Index {index} is out of valid 1-based bounds [{min}..{max}].", ErrorType.Validation);
    public static Error ItemNotFound(string id) =>
        new("Index.ItemNotFound", $"Item '{id}' was not found.", ErrorType.NotFound);
}

public static class DocumentErrors
{
    public static Error NotFound(string id) => new("Document.NotFound", $"Document workspace '{id}' was not found.", ErrorType.NotFound);
    public static Error InvalidWorkingDirectory(string path) => new("Document.InvalidWorkingDirectory", $"Working directory '{path}' is invalid.", ErrorType.Validation);
    public static Error EmptyTitle() => new("Document.EmptyTitle", "Document title cannot be empty.", ErrorType.Validation);
    public static Error InvalidPageNumber(int pageNumber, int pageCount) => new("Page.NotFound", $"Page {pageNumber} is outside valid document range [1..{pageCount}].", ErrorType.NotFound);
    public static Error InvalidConfidenceThreshold(double threshold) => new("Document.InvalidConfidenceThreshold", $"Confidence threshold {threshold:F2} must be within [0.05..0.99].", ErrorType.Validation);
    public static Error InvalidPdfFormat(string path, string reason) => new("Pdf.InvalidFormat", $"'{path}' is not a valid PDF file: {reason}", ErrorType.Validation);
    public static Error PdfFileNotFound(string path) => new("File.NotFound", $"Input PDF file '{path}' was not found.", ErrorType.NotFound);
    public static Error PdfRenderFailed(string path, int pageNumber, string reason) => new("Pdf.RenderFailed", $"Failed to render PDF '{path}' page {pageNumber}: {reason}", ErrorType.Failure);
    public static Error ProjectPdfAlreadyExists(string destPath) => new("Project.PdfConflict", $"Destination project PDF '{destPath}' already exists.", ErrorType.Conflict);
    public static Error ProjectNotFound(string projectDir) => new("Project.NotFound", $"No melomg project database was found in directory '{projectDir}'.", ErrorType.NotFound);
    public static Error PdfMoveFailed(string src, string dest, string reason) => new("Project.MoveFailed", $"Failed to move PDF from '{src}' to '{dest}': {reason}", ErrorType.Failure);
}

public static class FigureErrors
{
    public static Error NotFound(string id) => new("Figure.NotFound", $"Figure '{id}' was not found.", ErrorType.NotFound);

    public static Error InvalidCategory(string rawCategory) =>
        new("Figure.InvalidCategory", $"Figure category '{rawCategory}' must be one of: figure, diagram, chart.", ErrorType.Validation);

    public static Error InvalidBoundingBox(string reason) => new("Figure.InvalidBoundingBox", $"Invalid bounding box: {reason}", ErrorType.Validation);

    public static Error BoundingBoxOutOfPageBounds(double x, double y, double width, double height, double pageWidth, double pageHeight) =>
        new("Figure.BoundingBoxOutOfPageBounds", $"Bounding box ({x:F1}, {y:F1}, {width:F1}, {height:F1}) exceeds page dimensions ({pageWidth:F1} x {pageHeight:F1} pts).", ErrorType.Validation);

    public static Error InvalidConfidence(double confidence) => new("Figure.InvalidConfidence", $"Confidence score {confidence:F3} must be within [0.0..1.0].", ErrorType.Validation);
    public static Error EmptyLabel() => new("Figure.EmptyLabel", "Figure label cannot be empty.", ErrorType.Validation);
    public static Error EmptyCaption() => new("Figure.EmptyCaption", "Figure caption cannot be empty.", ErrorType.Validation);
    public static Error InvalidFigureNumber(string raw) =>
        new("Figure.InvalidFigureNumber", $"Figure number '{raw}' contains no valid identifier characters.", ErrorType.Validation);
    public static Error InvalidFormat(string format) => new("Figure.InvalidFormat", $"Output image format '{format}' is unsupported. Use 'png' or 'jpg'.", ErrorType.Validation);
    public static Error InvalidDpi(int dpi) => new("Figure.InvalidDpi", $"DPI {dpi} must be within [72..600].", ErrorType.Validation);
    public static Error InvalidPadding(int paddingPx) => new("Figure.InvalidPadding", $"Padding {paddingPx} px must be within [0..100].", ErrorType.Validation);
    public static Error InvalidQuality(string raw) => new("Figure.InvalidQuality", $"Image quality '{raw}' must be an integer in [1..100] or one of: low, medium, high, max.", ErrorType.Validation);
    public static Error InvalidNamingMode(string raw) => new("Figure.InvalidNamingMode", $"Naming mode '{raw}' must be 'number' or 'id'.", ErrorType.Validation);
    public static Error InvalidAddress(string addr, string reason) => new("Figure.InvalidAddress", $"Invalid address '{addr}': {reason}", ErrorType.Validation);
    public static Error NoFiguresMatch(string target) => new("Document.EmptyFigures", $"No figures matched extraction target '{target}'.", ErrorType.Conflict);
    public static Error InvalidObfuscationStyle(string raw) => new("Figure.InvalidObfuscationStyle", $"Obfuscation style '{raw}' is unsupported. Use solid, blur, pixelate, or whiteout.", ErrorType.Validation);
    public static Error InvalidColorHex(string raw) => new("Figure.InvalidColorHex", $"Color '{raw}' must be a 6-digit hex RGB value like '#1E293B'.", ErrorType.Validation);
    public static Error MaskNotFound(int index, int count) => new("Figure.MaskNotFound", $"Mask index #{index} was not found (figure currently has {count} mask(s)).", ErrorType.NotFound);

    public static Error NumberNotFound(string query, DocumentVocabulary vocab, IEnumerable<string> availableNumbers)
    {
        var nums = availableNumbers.Where(n => !string.IsNullOrWhiteSpace(n)).Take(10).ToArray();
        string vocabInfo = string.IsNullOrWhiteSpace(vocab.PrimaryPrefix)
            ? "No figure caption prefix detected"
            : $"PDF uses '{vocab.PrimaryPrefix}'";
        string availInfo = nums.Length > 0
            ? $"available numbers: {string.Join(", ", nums)}"
            : "no numbered figures detected; use #f<index>";
        return new Error(
            "Figure.NotFound",
            $"No figure matched number '{query}'.",
            ErrorType.NotFound,
            Hint: $"{vocabInfo} ({availInfo}).");
    }

    public static Error AmbiguousNumber(string query, IEnumerable<string> candidateAddresses)
    {
        string list = string.Join(", ", candidateAddresses);
        return new Error(
            "Figure.AmbiguousNumber",
            $"Figure number '{query}' matches multiple figures.",
            ErrorType.Conflict,
            Hint: $"Target explicitly using: {list}");
    }
}

public static class ModelErrors
{
    public static Error NotFound(string path) => new("Model.NotFound", $"ONNX model file '{path}' was not found.", ErrorType.NotFound);
    public static Error InvalidModelFormat(string path, string reason) => new("Model.InvalidModelFormat", $"File '{path}' is not a valid ONNX model: {reason}", ErrorType.Validation);
    public static Error InvalidThreshold(string name, double value, double min, double max) => new("Model.InvalidThreshold", $"ONNX {name} threshold {value:F2} must be within [{min:F2}..{max:F2}].", ErrorType.Validation);
}

public static class RuntimeErrors
{
    public static Error InvalidProvider(string raw) => new("Runtime.InvalidProvider", $"Execution provider '{raw}' is unsupported. Allowed values: directml, cpu.", ErrorType.Validation);
    public static Error InstallFailed(string reason) => new("Runtime.InstallFailed", $"Failed to install ONNX Runtime: {reason}", ErrorType.Failure);
    public static Error LoadFailed(string reason) => new("Runtime.LoadFailed", $"Failed to load ONNX Runtime: {reason}", ErrorType.Failure);
    public static Error UninstallFailed(string reason) => new("Runtime.UninstallFailed", $"Failed to uninstall ONNX Runtime: {reason}", ErrorType.Failure);
}

public static class LintErrors
{
    public static Error Failed(int errorCount) => new("Lint.Failed", $"Document lint failed with {errorCount} error(s).", ErrorType.Validation);
}
