using Spectre.Console;

namespace Elevate.Audit.Infrastructure;

/// <summary>Results on stdout, progress and prompts on stderr, so `--json -` and redirection stay clean.</summary>
public sealed class Terminal
{
    public Terminal(bool quiet, bool noColor)
    {
        Quiet = quiet;
        var plain = noColor || Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 };
        var colors = plain ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect;
        var ansi = plain ? AnsiSupport.No : AnsiSupport.Detect;
        Stdout = AnsiConsole.Create(new AnsiConsoleSettings { ColorSystem = colors, Ansi = ansi, Out = new AnsiConsoleOutput(Console.Out), Interactive = InteractionSupport.No });
        Stderr = AnsiConsole.Create(new AnsiConsoleSettings { ColorSystem = colors, Ansi = ansi, Out = new AnsiConsoleOutput(Console.Error), Interactive = InteractionSupport.No });
    }

    public bool Quiet { get; }

    public IAnsiConsole Stdout { get; }

    public IAnsiConsole Stderr { get; }

    public void Note(string plainText)
    {
        if (!Quiet)
        {
            Stderr.MarkupLine($"[grey]{Markup.Escape(plainText)}[/]");
        }
    }

    public void Say(string plainText) => Stderr.MarkupLine($"[blue]{Markup.Escape(plainText)}[/]");
}
