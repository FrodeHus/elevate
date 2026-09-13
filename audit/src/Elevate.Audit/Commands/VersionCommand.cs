using System.CommandLine;
using Elevate.Audit.Infrastructure;

namespace Elevate.Audit.Commands;

public static class VersionCommand
{
    public static Command Create()
    {
        var command = new Command("version", "Print the tool version and runtime.");
        command.SetAction(_ =>
        {
            Console.Out.WriteLine($"{AppInfo.Name} {AppInfo.Version} ({AppInfo.Runtime})");
            return ExitCodes.Ok;
        });
        return command;
    }
}
