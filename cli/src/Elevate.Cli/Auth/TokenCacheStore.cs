using System.Runtime.InteropServices;
using Elevate.Cli.Infrastructure;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Elevate.Cli.Auth;

/// <summary>
/// The MSAL token cache on disk, one file shared by every client id (MSAL keys entries by client
/// id, so the own-app, first-party and custom registrations never see each other's tokens).
/// Protected the way each platform protects secrets: DPAPI on Windows, the login keychain on
/// macOS, the Secret Service keyring on Linux. On Linux without a keyring (SSH, containers) the
/// cache falls back to a plain file only when the user opted in with
/// <c>elevate config set unprotected-cache true</c>.
/// </summary>
public sealed class TokenCacheStore
{
    public const string FileName = "msal.cache";

    private readonly Lazy<Task<MsalCacheHelper>> _helper;

    public TokenCacheStore(string directory, bool unprotectedOnLinux)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
        _helper = new Lazy<Task<MsalCacheHelper>>(() => CreateAsync(directory, unprotectedOnLinux));
    }

    public string Directory { get; }

    public async Task RegisterAsync(IPublicClientApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var helper = await _helper.Value.ConfigureAwait(false);
        helper.RegisterCache(app.UserTokenCache);
    }

    private static async Task<MsalCacheHelper> CreateAsync(string directory, bool unprotectedOnLinux)
    {
        System.IO.Directory.CreateDirectory(directory);
        var builder = new StorageCreationPropertiesBuilder(FileName, directory)
            .WithMacKeyChain("no.reothor.elevate-cli", "msal")
            .WithLinuxKeyring(
                "no.reothor.elevate-cli",
                MsalCacheHelper.LinuxKeyRingDefaultCollection,
                "Elevate CLI token cache",
                new KeyValuePair<string, string>("Version", "1"),
                new KeyValuePair<string, string>("Product", "elevate-cli"));
        if (unprotectedOnLinux && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            builder = builder.WithLinuxUnprotectedFile();
        }

        var properties = builder.Build();
        var helper = await MsalCacheHelper.CreateAsync(properties).ConfigureAwait(false);
        try
        {
            helper.VerifyPersistence();
        }
        catch (MsalCachePersistenceException e)
        {
            var hint = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? " No keyring is reachable in this session. Either unlock the desktop keyring, or run "
                  + "'elevate config set unprotected-cache true' to keep the cache in a plain file under "
                  + directory + " (readable by anyone with your user's file access)."
                : string.Empty;
            throw new CliException("The token cache cannot be stored securely: " + FirstLine(e.Message) + hint);
        }

        return helper;
    }

    private static string FirstLine(string text) => (text ?? string.Empty).Split('\n', 2)[0].Trim();
}
