using Elevate.Core.Managed;
using FluentAssertions;

namespace Elevate.Core.Tests.Managed;

public class RegistryManagedSourceTests
{
    private sealed class FakeRegistry : IRegistryView
    {
        public Dictionary<string, object?> Values { get; } = [];

        public Dictionary<string, List<string>> Lists { get; } = [];

        public object? Value(string subKey, string name) => Values.TryGetValue(name, out var value) ? value : null;

        public IReadOnlyList<string>? ListValues(string subKey)
        {
            if (!Lists.TryGetValue(subKey, out var entries))
            {
                return null;
            }

            return entries
                .Select(entry =>
                {
                    var parts = entry.Split(':', 2);
                    return (Name: parts[0], Value: parts[1]);
                })
                .OrderBy(pair => int.TryParse(pair.Name, out var n) ? n : int.MaxValue)
                .ThenBy(pair => pair.Name, StringComparer.Ordinal)
                .Select(pair => pair.Value)
                .ToList();
        }
    }

    [Fact]
    public void MachineWinsPerKey()
    {
        var machine = new FakeRegistry();
        machine.Values["ClientId"] = "A";
        var user = new FakeRegistry();
        user.Values["ClientId"] = "B";
        user.Values["DisableUpdateCheck"] = 1;

        var source = new RegistryManagedSource(machine, user);
        source.String(ManagedKey.ClientId).Should().Be("A");
        source.Bool(ManagedKey.DisableUpdateCheck).Should().BeTrue();
    }

    [Fact]
    public void ListsComeFromSubKeyValuesInNumericOrder()
    {
        var machine = new FakeRegistry();
        machine.Lists[@"SOFTWARE\Policies\Reothor\Elevate\AllowedTenants"] = ["10:c", "2:b", "1:a"];
        var user = new FakeRegistry();

        var source = new RegistryManagedSource(machine, user);
        source.List(ManagedKey.AllowedTenants).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void MultiStringIsAcceptedForLists()
    {
        var machine = new FakeRegistry();
        machine.Values["AllowedTenants"] = new[] { "a", "b" };
        var user = new FakeRegistry();

        var source = new RegistryManagedSource(machine, user);
        source.List(ManagedKey.AllowedTenants).Should().Equal("a", "b");
    }

    [Fact]
    public void DwordAndStringBooleans()
    {
        var machine = new FakeRegistry();
        var user = new FakeRegistry();
        var source = new RegistryManagedSource(machine, user);

        machine.Values["DisableUpdateCheck"] = 1;
        source.Bool(ManagedKey.DisableUpdateCheck).Should().BeTrue();

        machine.Values["DisableUpdateCheck"] = 0;
        source.Bool(ManagedKey.DisableUpdateCheck).Should().BeFalse();

        machine.Values["DisableUpdateCheck"] = "true";
        source.Bool(ManagedKey.DisableUpdateCheck).Should().BeTrue();
    }

    [Fact]
    public void MultiStringProfilesJoinLines()
    {
        var machine = new FakeRegistry();
        machine.Values["ManagedProfiles"] = new[] { "{", "}" };
        var user = new FakeRegistry();

        var source = new RegistryManagedSource(machine, user);
        source.String(ManagedKey.ManagedProfiles).Should().Be("{\n}");
    }
}
