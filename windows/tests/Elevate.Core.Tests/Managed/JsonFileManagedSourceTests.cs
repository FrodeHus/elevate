using Elevate.Core.Managed;
using FluentAssertions;

namespace Elevate.Core.Tests.Managed;

public class JsonFileManagedSourceTests
{
    [Fact]
    public void ReadsKeysFromJsonObject()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{"ClientId":"11111111-2222-3333-4444-555555555555","DisableUpdateCheck":true,"AllowedTenants":["contoso.com"],"ManagedProfiles":{"version":1,"profiles":[]}}""");
        try
        {
            var source = new JsonFileManagedSource(path, _ => true);
            source.String(ManagedKey.ClientId).Should().Be("11111111-2222-3333-4444-555555555555");
            source.Bool(ManagedKey.DisableUpdateCheck).Should().BeTrue();
            source.List(ManagedKey.AllowedTenants).Should().Equal("contoso.com");
            source.String(ManagedKey.ManagedProfiles).Should().Be("""{"version":1,"profiles":[]}""");
            source.Origin.Should().Be(path);
            source.Warning.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UntrustedFileIsIgnoredWithWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{"ClientId":"11111111-2222-3333-4444-555555555555"}""");
        try
        {
            var source = new JsonFileManagedSource(path, _ => false);
            source.String(ManagedKey.ClientId).Should().BeNull();
            source.Bool(ManagedKey.DisableUpdateCheck).Should().BeNull();
            source.List(ManagedKey.AllowedTenants).Should().BeNull();
            source.Warning.Should().Contain("not owned by root or is writable by others");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileIsEmptyWithoutWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var source = new JsonFileManagedSource(path, _ => true);
        source.String(ManagedKey.ClientId).Should().BeNull();
        source.Bool(ManagedKey.DisableUpdateCheck).Should().BeNull();
        source.List(ManagedKey.AllowedTenants).Should().BeNull();
        source.Warning.Should().BeNull();
    }

    [Fact]
    public void NonIntegralNumberForBoolIsIgnoredNotThrown()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{"DisableUpdateCheck":1.5}""");
        try
        {
            var source = new JsonFileManagedSource(path, _ => true);
            source.Bool(ManagedKey.DisableUpdateCheck).Should().BeNull();

            var config = ManagedConfiguration.Load(source);
            config.DisableUpdateCheck.Should().BeFalse();
            config.KeysInEffect.Should().NotContain(ManagedKey.DisableUpdateCheck);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NullArrayItemIsDroppedNotThrown()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{"AllowedTenants":["contoso.com",null]}""");
        try
        {
            var source = new JsonFileManagedSource(path, _ => true);
            source.List(ManagedKey.AllowedTenants).Should().Equal("contoso.com");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MalformedJsonIsIgnoredWithWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{ not valid json");
        try
        {
            var source = new JsonFileManagedSource(path, _ => true);
            source.String(ManagedKey.ClientId).Should().BeNull();
            source.Warning.Should().NotBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
