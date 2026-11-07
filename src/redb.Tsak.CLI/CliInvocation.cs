using System.CommandLine;

namespace redb.Tsak.CLI;

/// <summary>
/// One run of a command's action: the parse result it reads its options from, the token Ctrl+C cancels, and the exit
/// code it sets (0 unless it sets another). Created by <see cref="Program.Handle(Command, Func{CliInvocation, Task})"/>.
/// </summary>
internal sealed class CliInvocation(ParseResult parseResult, CancellationToken cancellationToken)
{
    /// <summary>The parsed command line.</summary>
    public ParseResult ParseResult { get; } = parseResult;

    /// <summary>Cancelled by Ctrl+C.</summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>The process exit code the command returns.</summary>
    public int ExitCode { get; set; }
}
