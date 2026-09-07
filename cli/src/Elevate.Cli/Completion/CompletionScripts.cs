using System.CommandLine;
using System.Globalization;
using System.Text;
using Elevate.Cli.Infrastructure;

namespace Elevate.Cli.Completion;

/// <summary>
/// Static completion scripts generated from the command tree: subcommands and options complete,
/// positional values (role names, profiles) do not, since they need a signed-in session.
/// </summary>
public static class CompletionScripts
{
    public static string Generate(Command root, string shell)
    {
        ArgumentNullException.ThrowIfNull(root);
        return (shell ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "bash" => Bash(root),
            "zsh" => Zsh(root),
            "fish" => Fish(root),
            "powershell" or "pwsh" => PowerShell(root),
            var other => throw new CliException($"Unknown shell '{other}'. Use bash, zsh, fish or powershell.", ExitCodes.Usage),
        };
    }

    /// <summary>Every command path ("", "tenants", "tenants add") with the words that may follow it.</summary>
    internal static IReadOnlyList<(string Path, IReadOnlyList<string> Words)> Paths(Command root)
    {
        var result = new List<(string, IReadOnlyList<string>)>();
        void Walk(Command command, string path, IEnumerable<Option> inherited)
        {
            var recursive = inherited.Concat(command.Options.Where(o => o.Recursive)).ToList();
            var words = command.Subcommands.Select(c => c.Name)
                .Concat(command.Options.Concat(recursive).Where(o => !o.Hidden).SelectMany(o => o.Aliases.Prepend(o.Name)))
                .Concat(command == root ? [] : ["--help"])
                // The help option also answers to /h and /?; those are not words a shell completes.
                .Where(w => !w.StartsWith("/", StringComparison.Ordinal))
                .Distinct()
                .ToList();
            result.Add((path, words));
            foreach (var sub in command.Subcommands)
            {
                Walk(sub, path.Length == 0 ? sub.Name : path + " " + sub.Name, recursive);
            }
        }

        Walk(root, string.Empty, []);
        return result;
    }

    private static string Bash(Command root)
    {
        var sb = new StringBuilder();
        sb.Append("# bash completion for elevate. Load with: source <(elevate completion bash)\n");
        sb.Append("_elevate() {\n  local cur words path\n  cur=\"${COMP_WORDS[COMP_CWORD]}\"\n  path=\"\"\n");
        sb.Append("  for ((i=1; i<COMP_CWORD; i++)); do\n    case \"${COMP_WORDS[i]}\" in\n      -*) ;;\n      *) path=\"${path:+$path }${COMP_WORDS[i]}\" ;;\n    esac\n  done\n");
        sb.Append("  case \"$path\" in\n");
        foreach (var (path, words) in Paths(root))
        {
            sb.Append(CultureInfo.InvariantCulture, $"    \"{path}\") words=\"{string.Join(' ', words)}\" ;;\n");
        }

        sb.Append("    *) words=\"\" ;;\n  esac\n");
        sb.Append("  COMPREPLY=( $(compgen -W \"$words\" -- \"$cur\") )\n}\ncomplete -F _elevate elevate\n");
        return sb.ToString();
    }

    private static string Zsh(Command root)
    {
        var sb = new StringBuilder();
        sb.Append("#compdef elevate\n# zsh completion for elevate. Load with: source <(elevate completion zsh)\n");
        sb.Append("_elevate() {\n  local -a words_out\n  local path=\"\"\n  local i\n  for ((i=2; i<CURRENT; i++)); do\n    case \"${words[i]}\" in\n      -*) ;;\n      *) path=\"${path:+$path }${words[i]}\" ;;\n    esac\n  done\n");
        sb.Append("  case \"$path\" in\n");
        foreach (var (path, words) in Paths(root))
        {
            sb.Append(CultureInfo.InvariantCulture, $"    \"{path}\") words_out=({string.Join(' ', words)}) ;;\n");
        }

        sb.Append("    *) words_out=() ;;\n  esac\n  compadd -- \"${words_out[@]}\"\n}\ncompdef _elevate elevate\n");
        return sb.ToString();
    }

    private static string Fish(Command root)
    {
        var sb = new StringBuilder();
        sb.Append("# fish completion for elevate. Load with: elevate completion fish | source\n");
        sb.Append("function __elevate_path\n  set -l tokens (commandline -opc)\n  set -e tokens[1]\n  set -l path\n  for t in $tokens\n    switch $t\n      case '-*'\n      case '*'\n        set -a path $t\n    end\n  end\n  echo $path\nend\n");
        foreach (var (path, words) in Paths(root))
        {
            foreach (var word in words)
            {
                var condition = $"test \"(__elevate_path)\" = \"{path}\"";
                sb.Append(CultureInfo.InvariantCulture, $"complete -c elevate -n '{condition}' ");
                sb.Append(word.StartsWith("--", StringComparison.Ordinal)
                    ? $"-l {word[2..]}"
                    : word.StartsWith('-') ? $"-s {word[1..]}" : $"-a {word}");
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string PowerShell(Command root)
    {
        var sb = new StringBuilder();
        sb.Append("# PowerShell completion for elevate. Load with: elevate completion powershell | Out-String | Invoke-Expression\n");
        sb.Append("Register-ArgumentCompleter -Native -CommandName elevate -ScriptBlock {\n  param($wordToComplete, $commandAst, $cursorPosition)\n");
        sb.Append("  $tokens = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.ToString() })\n");
        sb.Append("  if ($wordToComplete -ne '' -and $tokens.Count -gt 0) { $tokens = $tokens[0..($tokens.Count - 2)] }\n");
        sb.Append("  $path = ($tokens | Where-Object { $_ -notlike '-*' }) -join ' '\n");
        sb.Append("  $table = @{\n");
        foreach (var (path, words) in Paths(root))
        {
            sb.Append(CultureInfo.InvariantCulture, $"    '{path}' = @({string.Join(", ", words.Select(w => $"'{w}'"))})\n");
        }

        sb.Append("  }\n  $words = $table[$path]\n  if (-not $words) { return }\n");
        sb.Append("  $words | Where-Object { $_ -like \"$wordToComplete*\" } | ForEach-Object {\n");
        sb.Append("    [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)\n  }\n}\n");
        return sb.ToString();
    }
}
