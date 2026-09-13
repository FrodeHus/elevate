using System.CommandLine;
using Elevate.Audit.Infrastructure;
using Elevate.Audit.Update;
using Elevate.Core.Networking;

namespace Elevate.Audit.Commands;

public static class UpdateCommand
{
    public static Command Create()
    {
        var command = new Command("update", "Check GitHub for a newer release of elevate-audit.");
        command.SetAction(async (_, ct) =>
        {
            var checker = new ReleaseChecker(new HttpClientAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }));
            var latest = await checker.LatestAsync(ct).ConfigureAwait(false);
            var current = AppInfo.Version;
            if (latest is null)
            {
                Console.Out.WriteLine($"{AppInfo.Name} {current}; no release with an elevate-audit build was found.");
            }
            else if (ReleaseChecker.IsNewer(latest.Tag, current))
            {
                Console.Out.WriteLine($"{AppInfo.Name} {current}; {latest.Version} is available: {latest.Url}");
            }
            else
            {
                Console.Out.WriteLine($"{AppInfo.Name} {current} is up to date.");
            }

            return ExitCodes.Ok;
        });
        return command;
    }
}
