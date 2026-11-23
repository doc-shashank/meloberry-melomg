using Melomg.Domain.Common;

namespace Melomg.Cli.Abstractions;

public interface IConsole
{
    TextWriter Out { get; }
    TextWriter Error { get; }
    TextReader In { get; }
    bool IsInputRedirected { get; }
}

public sealed class SystemConsole : IConsole
{
    public TextWriter Out => Console.Out;
    public TextWriter Error => Console.Error;
    public TextReader In => Console.In;
    public bool IsInputRedirected => Console.IsInputRedirected;
}

public sealed class TestConsole : IConsole
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();
    private TextReader _in;

    public TestConsole(string? inputText = null)
    {
        _in = new StringReader(inputText ?? string.Empty);
        IsInputRedirected = inputText is not null;
    }

    public TextWriter Out => _out;
    public TextWriter Error => _error;
    public TextReader In => _in;
    public bool IsInputRedirected { get; private set; }

    public string StandardOutput => _out.ToString();
    public string StandardError => _error.ToString();

    public void SetInput(string inputText)
    {
        _in = new StringReader(inputText);
        IsInputRedirected = true;
    }
}

public sealed record GlobalCliOptions(
    bool Silent,
    bool Json,
    string? OutputDirectory = null,
    string? ModelPath = null,
    string? AppDataDirectory = null);

public sealed class CliInvocationContext(
    IConsole console,
    GlobalCliOptions globalOptions,
    IReadOnlyList<string> positionalArgs,
    IReadOnlyDictionary<string, string?> namedOptions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> multiValueOptions,
    CancellationToken cancellationToken)
{
    public IConsole Console { get; } = console;
    public GlobalCliOptions GlobalOptions { get; } = globalOptions;
    public IReadOnlyList<string> PositionalArgs { get; } = positionalArgs;
    public IReadOnlyDictionary<string, string?> NamedOptions { get; } = namedOptions;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MultiValueOptions { get; } = multiValueOptions;
    public CancellationToken CancellationToken { get; } = cancellationToken;
}

public interface ICommandHandler
{
    Task<int> InvokeAsync(CliInvocationContext context);
}

public enum CommandCategory
{
    Discovery,
    Documents,
    Figures,
    Extraction,
    Models,
    Diagnostics
}

public sealed record CommandDescriptor(
    string Verb,
    string Usage,
    string Description,
    Type HandlerType,
    CommandCategory Category = CommandCategory.Diagnostics,
    string? DetailedNotes = null,
    IReadOnlyList<string>? Examples = null);

public abstract class CliCommand
{
    protected CliCommand(CommandDescriptor descriptor) => Descriptor = descriptor;

    public CommandDescriptor Descriptor { get; }
}

public static class ExitCodes
{
    public const int Success = 0;
    public const int GeneralError = 1;
    public const int ValidationError = 2;
    public const int NotFound = 3;
    public const int Conflict = 4;
    public const int Unauthorized = 5;
}

public static class CliResultExtensions
{
    public static int ToExitCode(this Result result, IConsole console, bool json = false)
    {
        if (result.IsSuccess)
            return ExitCodes.Success;

        var error = result.Error!;
        if (json)
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(
                new CliJsonErrorEnvelope(false, new CliJsonErrorBody(error.Code, error.Description, error.Type.ToString(), error.Path, error.Hint)),
                MelomgCliJsonContext.Default.CliJsonErrorEnvelope);
            console.Error.WriteLine(payload);
        }
        else
        {
            console.Error.WriteLine($"Error [{error.Code}]: {error.Description}");
            if (!string.IsNullOrWhiteSpace(error.Hint))
                console.Error.WriteLine($"Hint: {error.Hint}");
        }

        return error.Type switch
        {
            ErrorType.Validation => ExitCodes.ValidationError,
            ErrorType.NotFound => ExitCodes.NotFound,
            ErrorType.Conflict => ExitCodes.Conflict,
            ErrorType.Unauthorized or ErrorType.Forbidden => ExitCodes.Unauthorized,
            _ => ExitCodes.GeneralError
        };
    }

    public static int WriteValidationError(
        IConsole console,
        string code,
        string description,
        bool json = false,
        string? path = null,
        string? hint = null) =>
        Result.Failure(new Error(code, description, ErrorType.Validation, path, hint)).ToExitCode(console, json);
}
