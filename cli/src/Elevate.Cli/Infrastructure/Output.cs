using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Elevate.Cli.Infrastructure;

/// <summary>
/// The two consoles: results on stdout, everything else (spinners, sign-in prompts, warnings) on
/// stderr, so <c>elevate roles --json | jq</c> and <c>elevate status > file</c> stay clean.
/// </summary>
public sealed class Output
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public Output(bool json, bool quiet, bool noColor)
    {
        Json = json;
        Quiet = quiet;
        var colors = noColor || Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 }
            ? ColorSystemSupport.NoColors
            : ColorSystemSupport.Detect;
        Stdout = AnsiConsole.Create(new AnsiConsoleSettings
        {
            ColorSystem = colors,
            Out = new AnsiConsoleOutput(Console.Out),
            Interactive = InteractionSupport.No,
        });
        Stderr = AnsiConsole.Create(new AnsiConsoleSettings
        {
            ColorSystem = colors,
            Out = new AnsiConsoleOutput(Console.Error),
        });
    }

    /// <summary>Whether the command was asked for machine-readable output.</summary>
    public bool Json { get; }

    public bool Quiet { get; }

    public IAnsiConsole Stdout { get; }

    public IAnsiConsole Stderr { get; }

    /// <summary>True when a person can answer a prompt: stdin and stderr are terminals and --json is off.</summary>
    public bool CanPrompt => !Json && !Console.IsInputRedirected && !Console.IsErrorRedirected;

    public void Write(IRenderable renderable) => Stdout.Write(renderable);

    public void WriteLine(string markup) => Stdout.MarkupLine(markup);

    public void Plain(string text) => Console.Out.WriteLine(text);

    public void WriteJson<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    /// <summary>A progress note on stderr; silent under --quiet and --json.</summary>
    public void Note(string markup)
    {
        if (!Quiet && !Json)
        {
            Stderr.MarkupLine($"[grey]{markup}[/]");
        }
    }

    public void Warn(string markup)
    {
        if (!Quiet)
        {
            Stderr.MarkupLine($"[yellow]{markup}[/]");
        }
    }

    public void Error(string text) => Stderr.MarkupLine($"[red]{Markup.Escape(text)}[/]");

    /// <summary>Runs <paramref name="work"/> under a spinner when stderr is a terminal, plainly otherwise.</summary>
    public async Task<T> StatusAsync<T>(string title, Func<Task<T>> work)
    {
        if (Quiet || Json || Console.IsErrorRedirected)
        {
            return await work().ConfigureAwait(false);
        }

        return await Stderr.Status().Spinner(Spinner.Known.Dots).StartAsync(Markup.Escape(title), _ => work()).ConfigureAwait(false);
    }

    public Task StatusAsync(string title, Func<Task> work) => StatusAsync(title, async () =>
    {
        await work().ConfigureAwait(false);
        return true;
    });
}
