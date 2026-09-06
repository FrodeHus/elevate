using Elevate.App.Services;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Microsoft.Identity.Client;

namespace Elevate.App.Auth;

/// <summary>
/// A public client that is not ours to configure: the Azure CLI or Azure PowerShell app, or a
/// custom registration typed by the user. No broker; the system browser at <c>http://localhost</c>.
/// Resource reads ask for the resource's <c>.default</c>, since a first-party app's consented
/// scopes are whatever Microsoft granted it, not the PIM scopes by name.
/// </summary>
public sealed class FirstPartyTokenProvider : MsalProviderBase
{
    private const string GraphDefault = "https://graph.microsoft.com/.default";

    public FirstPartyTokenProvider(SignInMethod method, TokenCache cache, InteractiveGate gate, Func<IntPtr> parentWindow)
        : base(Build(method, parentWindow), cache, gate)
    {
        Method = method;
    }

    protected override SignInMethod Method { get; }

    protected override IReadOnlyList<string> SignInScopes { get; } = [GraphDefault];

    protected override IReadOnlyList<string> Requested(IReadOnlyList<string> scopes) => [ResourceDefault(scopes)];

    protected override AcquireTokenInteractiveParameterBuilder Configure(AcquireTokenInteractiveParameterBuilder builder) =>
        builder.WithUseEmbeddedWebView(false);

    /// <summary><c>https://host/.default</c> for the resource the scopes belong to.</summary>
    public static string ResourceDefault(IReadOnlyList<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var first = scopes.FirstOrDefault(s => s.Contains("://", StringComparison.Ordinal));
        if (first is not null && Uri.TryCreate(first, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Host}/.default";
        }

        return GraphDefault;
    }

    private static IPublicClientApplication Build(SignInMethod method, Func<IntPtr> parentWindow)
    {
        ArgumentNullException.ThrowIfNull(parentWindow);
        var clientId = method.ClientId ?? throw new PimException(PimErrorKind.Unexpected, "Unsupported sign-in method");
        if (!AppSettings.IsValidClientId(clientId))
        {
            throw new PimException(PimErrorKind.Unexpected, "Enter the custom app's application (client) ID as a GUID");
        }

        try
        {
            return PublicClientApplicationBuilder.Create(clientId.Trim())
                .WithAuthority(AzureCloudInstance.AzurePublic, "organizations")
                .WithRedirectUri(AppSettings.LoopbackRedirectUri)
                .WithParentActivityOrWindow(parentWindow)
                .Build();
        }
        catch (MsalException e)
        {
            throw Map(e);
        }
    }
}

/// <summary>Creates first-party and custom providers on demand, one per client id, for the life of the process.</summary>
public sealed class FirstPartyProviderRegistry : IFirstPartyProviders
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FirstPartyTokenProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TokenCache _cache;
    private readonly InteractiveGate _interactive;
    private readonly Func<IntPtr> _parentWindow;

    public FirstPartyProviderRegistry(TokenCache cache, InteractiveGate interactive, Func<IntPtr> parentWindow)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(interactive);
        ArgumentNullException.ThrowIfNull(parentWindow);
        _cache = cache;
        _interactive = interactive;
        _parentWindow = parentWindow;
    }

    public ITokenProvider? Provider(SignInMethod method)
    {
        if (method.ClientId is not { } clientId || !AppSettings.IsValidClientId(clientId))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_providers.TryGetValue(clientId, out var provider))
            {
                provider = new FirstPartyTokenProvider(method, _cache, _interactive, _parentWindow);
                _providers[clientId] = provider;
            }

            return provider;
        }
    }

    public IReadOnlyCollection<ITokenProvider> Known
    {
        get
        {
            lock (_gate)
            {
                return [.. _providers.Values];
            }
        }
    }
}
