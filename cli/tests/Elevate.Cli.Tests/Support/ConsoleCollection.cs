namespace Elevate.Cli.Tests.Support;

/// <summary>
/// The tests that run the command tree redirect <see cref="Console.Out"/> and
/// <see cref="Console.Error"/>, which are process-wide: they share one collection so xUnit never
/// runs two of them at once and lets one capture the other's output.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "console";
}
