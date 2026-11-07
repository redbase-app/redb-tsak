using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;

namespace redb.Tsak.CLI;

/// <summary>
/// The parse errors of a command line whose only problem is required arguments left out. System.CommandLine reports
/// one error per missing argument, but its text names the command and not the argument ("Required argument missing for
/// command: 'get'."), so a command with two arguments printed the same line twice
/// (https://github.com/dotnet/command-line-api/pull/1993 would name the argument; not merged in 2.0). These errors are
/// reported here, one line per argument with its name, followed by the help of the command as the library does. Any
/// other parse error (an unknown command, a bad value, a missing required option) goes to the library's own
/// <see cref="System.CommandLine.Invocation.ParseErrorAction"/>, which keeps its "Did you mean" suggestions.
/// </summary>
internal static class MissingArguments
{
    /// <summary>
    /// The arguments left out, when every parse error of <paramref name="parseResult"/> is a missing required argument;
    /// empty otherwise (no error, or another kind of error among them).
    /// </summary>
    public static IReadOnlyList<ArgumentResult> Of(ParseResult parseResult)
    {
        if (parseResult.Errors.Count == 0)
            return [];

        var missing = new List<ArgumentResult>();
        foreach (var error in parseResult.Errors)
        {
            if (error.SymbolResult is not ArgumentResult argument
                || argument.Tokens.Count >= argument.Argument.Arity.MinimumNumberOfValues)
                return [];
            missing.Add(argument);
        }
        return missing;
    }

    /// <summary>
    /// Writes one line per missing argument to the error output, then the help of the command, and returns the exit
    /// code of a parse error (1).
    /// </summary>
    public static int Report(ParseResult parseResult, IReadOnlyList<ArgumentResult> missing)
    {
        var error = parseResult.InvocationConfiguration.Error;
        foreach (var argument in missing)
            error.WriteLine($"Required argument '<{argument.Argument.Name}>' is missing for command '{CommandPath(argument)}'.");
        error.WriteLine();

        new HelpAction().Invoke(parseResult);
        return 1;
    }

    /// <summary>The command the argument belongs to, as typed: <c>route get</c>, without the root command.</summary>
    private static string CommandPath(ArgumentResult argument)
    {
        var names = new Stack<string>();
        for (var result = argument.Parent as CommandResult; result is { Parent: not null }; result = result.Parent as CommandResult)
            names.Push(result.Command.Name);
        return string.Join(" ", names);
    }
}
