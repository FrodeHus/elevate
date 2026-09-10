using System.CommandLine;
using System.Diagnostics;
using Elevate.Cli.Infrastructure;
using Elevate.Core.Auth;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary>
/// <c>consent</c>: the admin consent link for the configured registration. Same rules as the
/// apps' consent buttons: the <c>organizations</c> segment unless a tenant is named, the shared
/// app's consent page as the redirect when the shared id is in effect, <c>nativeclient</c> otherwise.
/// </summary>
public static class ConsentCommand
{
    public static Command Consent()
    {
        var tenant = new Option<string?>("--tenant", "-t") { Description = "Consent in this tenant: a tracked tenant's name, or any tenant id or domain. Default: the 'organizations' endpoint, which asks the administrator to choose." };
        var open = new Option<bool>("--open") { Description = "Also open the link in the default browser." };
        var command = new Command("consent", "Print the admin consent link an administrator grants once per tenant so Elevate can read PIM eligibility.") { tenant, open };
        command.SetAction((parse, _) =>
        {
            var context = CommandContext.From(parse);
            var settings = context.Session.Settings;
            if (!settings.IsConfigured)
            {
                throw new CliException(
                    "No client ID is configured. Run 'elevate config set client-id <application id>' (or 'elevate config set client-id shared') first.",
                    ExitCodes.Usage);
            }

            var segment = ResolveSegment(context, parse.GetValue(tenant));
            var url = SharedApp.AdminConsentUri(settings.ClientId, segment);
            var kind = ConfigCommands.ClientIdKind(settings);
            if (context.Output.Json)
            {
                context.Output.WriteJson(new { url = url.ToString(), tenant = segment, clientIdKind = kind });
            }
            else
            {
                context.Output.Plain(url.ToString());
                context.Output.Note(kind == "shared"
                    ? "For the shared Elevate app (no SLA). A Privileged Role, Application or Global Administrator opens this link once per tenant; expect Microsoft's unverified-publisher warning."
                    : "For your own registration. A Privileged Role, Application or Global Administrator opens this link once per tenant.");
            }

            if (parse.GetValue(open))
            {
                Open(url, context.Output);
            }

            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }

    /// <summary>
    /// The authority segment: a GUID or a domain is used as typed, anything else must match one
    /// tracked tenant, and no value means <c>organizations</c>.
    /// </summary>
    internal static string ResolveSegment(CommandContext context, string? tenant)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = (tenant ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "organizations";
        }

        if (Guid.TryParse(text, out _) || text.Contains('.', StringComparison.Ordinal))
        {
            return text;
        }

        return context.RequireTenant(text, null).TenantId;
    }

    private static void Open(Uri url, Output output)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url.ToString()) { UseShellExecute = false });
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            output.Warn($"Could not open a browser: {Markup.Escape(e.Message)}. Copy the link instead.");
        }
    }
}
