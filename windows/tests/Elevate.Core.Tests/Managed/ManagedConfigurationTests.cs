using Elevate.Core.Managed;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests.Managed;

public class ManagedConfigurationTests
{
    [Fact]
    public void EmptySourceIsEmpty()
    {
        var config = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>()));

        config.IsEmpty.Should().BeTrue();
        config.ClientId.Should().BeNull();
        config.DisableUpdateCheck.Should().BeFalse();
        config.AllowedSignInMethods.Should().BeNull();
        config.AllowedTenants.Should().BeNull();
        config.PinnedTenants.Should().BeEmpty();
    }

    [Fact]
    public void ClientIdIsValidatedAndLowercased()
    {
        var config = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["ClientId"] = " 11111111-2222-3333-4444-555555555555 ",
        }));
        config.ClientId.Should().Be("11111111-2222-3333-4444-555555555555");
        config.KeysInEffect.Should().Equal(ManagedKey.ClientId);

        var bad = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["ClientId"] = "not-a-guid",
        }));
        bad.ClientId.Should().BeNull();
        bad.KeysInEffect.Should().BeEmpty();
        bad.Warnings.Should().ContainSingle(w => w.StartsWith("ClientId:", StringComparison.Ordinal));

        var zero = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["ClientId"] = "00000000-0000-0000-0000-000000000000",
        }));
        zero.ClientId.Should().BeNull();
    }

    [Fact]
    public void DisableUpdateCheckAcceptsBoolAndStrings()
    {
        ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?> { ["DisableUpdateCheck"] = true })).DisableUpdateCheck.Should().BeTrue();
        ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?> { ["DisableUpdateCheck"] = "true" })).DisableUpdateCheck.Should().BeTrue();
        ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?> { ["DisableUpdateCheck"] = 1 })).DisableUpdateCheck.Should().BeTrue();

        var off = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?> { ["DisableUpdateCheck"] = false }));
        off.DisableUpdateCheck.Should().BeFalse();
        off.KeysInEffect.Should().Equal(ManagedKey.DisableUpdateCheck);
    }

    [Fact]
    public void AllowedMethodsParseCaseInsensitivelyAndDropUnknown()
    {
        var config = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["AllowedSignInMethods"] = new[] { "OwnApp", "azurecli", "saml" },
        }));
        config.AllowedSignInMethods.Should().BeEquivalentTo(new[] { SignInMethodKind.OwnApp, SignInMethodKind.AzureCLI });
        config.Warnings.Should().Equal("AllowedSignInMethods: unknown method 'saml' ignored");

        var csv = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["AllowedSignInMethods"] = "ownApp, custom",
        }));
        csv.AllowedSignInMethods.Should().BeEquivalentTo(new[] { SignInMethodKind.OwnApp, SignInMethodKind.Custom });

        var empty = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["AllowedSignInMethods"] = Array.Empty<string>(),
        }));
        empty.AllowedSignInMethods.Should().BeNull();
        empty.KeysInEffect.Should().NotContain(ManagedKey.AllowedSignInMethods);

        var allUnknown = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["AllowedSignInMethods"] = new[] { "saml" },
        }));
        allUnknown.AllowedSignInMethods.Should().BeNull();
    }

    [Fact]
    public void AllowedMethodsRejectNumericStringsAsUnknown()
    {
        var config = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["AllowedSignInMethods"] = new[] { "0", "ownApp" },
        }));
        config.AllowedSignInMethods.Should().BeEquivalentTo(new[] { SignInMethodKind.OwnApp });
        config.Warnings.Should().Equal("AllowedSignInMethods: unknown method '0' ignored");
    }

    [Fact]
    public void TenantListsKeepEntriesTrimmedAndDeduplicated()
    {
        var config = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["AllowedTenants"] = new[] { " contoso.com ", "contoso.com", "" },
            ["PinnedTenants"] = new[] { "Fabrikam.com" },
        }));
        config.AllowedTenants.Should().Equal("contoso.com");
        config.PinnedTenants.Should().Equal("fabrikam.com");
        config.KeysInEffect.Should().Equal(ManagedKey.AllowedTenants, ManagedKey.PinnedTenants);
    }

    [Fact]
    public void ProfilesUrlMustBeHttps()
    {
        var ok = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["ManagedProfilesUrl"] = "https://example.com/p.json",
        }));
        ok.ManagedProfilesUrl!.AbsoluteUri.Should().Be("https://example.com/p.json");

        var http = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["ManagedProfilesUrl"] = "http://example.com/p.json",
        }));
        http.ManagedProfilesUrl.Should().BeNull();
        http.Warnings.Should().Equal("ManagedProfilesUrl: only https URLs are accepted");
    }

    [Fact]
    public void ProfilesDocumentIsKeptRaw()
    {
        var config = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["ManagedProfiles"] = "{\"version\":1,\"profiles\":[]}",
        }));
        config.ManagedProfilesDocument.Should().Be("{\"version\":1,\"profiles\":[]}");
        config.KeysInEffect.Should().Equal(ManagedKey.ManagedProfiles);

        var blank = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["ManagedProfiles"] = "  ",
        }));
        blank.ManagedProfilesDocument.Should().BeNull();
    }

    [Fact]
    public void OriginIsRecorded()
    {
        var config = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>
        {
            ["ClientId"] = "11111111-2222-3333-4444-555555555555",
        }, origin: "unit"));
        config.Origin.Should().Be("unit");

        ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object?>(), origin: "unit")).Origin.Should().BeNull();
    }
}
