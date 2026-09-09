using Elevate.Cli.Infrastructure;

namespace Elevate.Cli.Completion;

/// <summary>
/// <c>elevate init &lt;shell&gt;</c>: a snippet that wraps the tools people run right after an
/// activation and, when one of them fails with an authorization error, prints how to activate and
/// retry in one go with <c>elevate run</c>. The wrapper passes stdin and stdout straight through
/// and only tees stderr, so pipes and interactive commands keep working; the exit code is the
/// command's own. Static text, so it can be sourced from a profile without a signed-in session.
/// </summary>
public static class ShellHooks
{
    /// <summary>The commands wrapped by default; each script has a function to add more.</summary>
    public static readonly IReadOnlyList<string> DefaultCommands = ["az", "kubectl", "terraform", "helm"];

    /// <summary>What the Azure control plane, Graph and AKS say when the token lacks the assignment.</summary>
    public static readonly IReadOnlyList<string> Patterns = ["AuthorizationFailed", "does not have authorization", "Authorization_RequestDenied"];

    public static string Generate(string shell) => (shell ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "bash" => Posix("bash", "rc=${PIPESTATUS[0]}"),
        "zsh" => Posix("zsh", "rc=${pipestatus[1]}"),
        "fish" => Fish(),
        "powershell" or "pwsh" => PowerShell(),
        var other => throw new CliException($"Unknown shell '{other}'. Use bash, zsh, fish or pwsh.", ExitCodes.Usage),
    };

    private static string GrepArguments => string.Join(' ', Patterns.Select(p => $"-e '{p}'"));

    private static string Hint => "elevate: not authorized. Activate what you need and retry in one go:";

    private static string Posix(string shell, string exitStatus)
    {
        var commands = string.Join(' ', DefaultCommands);
        return $$"""
            # elevate shell hook for {{shell}}. Load with: eval "$(elevate init {{shell}})"
            # Wraps {{commands}}: when one fails with an authorization error, a line on stderr
            # suggests 'elevate run'. Wrap another command with: elevate_hint_wrap <name>
            # Bypass a wrapper once with: command <name> ...
            __elevate_hint_run() {
              local tmp rc
              tmp=$(mktemp 2>/dev/null) || { command "$@"; return; }
              { command "$@" 2>&1 1>&3 | tee "$tmp" >&2; {{exitStatus}}; } 3>&1
              if [ "$rc" -ne 0 ] && grep -q {{GrepArguments}} "$tmp" 2>/dev/null; then
                printf '\n%s\n  elevate run --role <role> -- %s\n' '{{Hint}}' "$*" >&2
              fi
              rm -f "$tmp"
              return "$rc"
            }
            elevate_hint_wrap() {
              eval "$1() { __elevate_hint_run $1 \"\$@\"; }"
            }
            for __elevate_cmd in {{commands}}; do elevate_hint_wrap "$__elevate_cmd"; done
            unset __elevate_cmd

            """;
    }

    private static string Fish()
    {
        var commands = string.Join(' ', DefaultCommands);
        return $$"""
            # elevate shell hook for fish. Load with: elevate init fish | source
            # Wraps {{commands}}: when one fails with an authorization error, a line on stderr
            # suggests 'elevate run'. Wrap another command with: elevate_hint_wrap <name>
            # Bypass a wrapper once with: command <name> ...
            function __elevate_hint_run
              set -l tmp (mktemp 2>/dev/null)
              or begin
                command $argv
                return
              end
              command $argv 2>| tee $tmp >&2
              set -l rc $pipestatus[1]
              if test $rc -ne 0; and grep -q {{GrepArguments}} $tmp 2>/dev/null
                printf '\n%s\n  elevate run --role <role> -- %s\n' '{{Hint}}' "$argv" >&2
              end
              rm -f $tmp
              return $rc
            end
            function elevate_hint_wrap
              eval "function $argv[1]; __elevate_hint_run $argv[1] \$argv; end"
            end
            for __elevate_cmd in {{commands}}
              elevate_hint_wrap $__elevate_cmd
            end
            set -e __elevate_cmd

            """;
    }

    private static string PowerShell()
    {
        var commands = string.Join(", ", DefaultCommands.Select(c => $"'{c}'"));
        var pattern = string.Join('|', Patterns);
        return $$"""
            # elevate shell hook for PowerShell. Load with: elevate init pwsh | Out-String | Invoke-Expression
            # Wraps {{string.Join(", ", DefaultCommands)}}: when one fails with an authorization error, a line
            # on stderr suggests 'elevate run'. Wrap another command with: Register-ElevateHint <name>
            # Bypass a wrapper once with: & (Get-Command <name> -CommandType Application) ...
            function global:Invoke-ElevateHintRun {
              param([Parameter(Mandatory)][string]$Command, [Parameter(ValueFromRemainingArguments)][string[]]$Rest = @())
              $app = Get-Command $Command -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
              if (-not $app) {
                [Console]::Error.WriteLine("$Command was not found on PATH")
                $global:LASTEXITCODE = 127
                return
              }
              $denied = $false
              & $app.Source @Rest 2>&1 | ForEach-Object {
                if ($_ -is [System.Management.Automation.ErrorRecord]) {
                  $line = $_.ToString()
                  if ($line -match '{{pattern}}') { $denied = $true }
                  [Console]::Error.WriteLine($line)
                } else {
                  $_
                }
              }
              $rc = $LASTEXITCODE
              if ($rc -ne 0 -and $denied) {
                [Console]::Error.WriteLine("`n{{Hint}}`n  elevate run --role <role> -- $Command $($Rest -join ' ')")
              }
              $global:LASTEXITCODE = $rc
            }
            function global:Register-ElevateHint {
              param([Parameter(Mandatory)][string]$Name)
              Set-Item -Path "function:global:$Name" -Value ([scriptblock]::Create("Invoke-ElevateHintRun $Name @args"))
            }
            foreach ($elevateCmd in {{commands}}) { Register-ElevateHint $elevateCmd }
            Remove-Variable elevateCmd

            """;
    }
}
