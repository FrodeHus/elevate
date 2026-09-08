using Elevate.Core.Coordination;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>NewRoleTrackerTests</c>.</summary>
public class NewRoleTrackerTests
{
    private static RoleKey Key(string n) => new("i", "t", new EntraDirectoryScope(n, "/"));

    private static HashSet<RoleKey> Keys(params string[] names) => [.. names.Select(Key)];

    [Fact]
    public void FirstObserveBaselinesWithoutReportingAdditions()
    {
        var t = new NewRoleTracker();
        var added = t.Observe(Keys("a", "b"));
        added.Should().BeEmpty();
        t.Seen.Should().BeEquivalentTo(Keys("a", "b"));
        t.New.Should().BeEmpty();
    }

    [Fact]
    public void AdditionsAreReportedOnceAndMarkedNew()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a"));
        var added = t.Observe(Keys("a", "c", "b"));
        added.Should().BeEquivalentTo(Keys("b", "c"));
        t.IsNew(Key("b")).Should().BeTrue();
        t.IsNew(Key("c")).Should().BeTrue();
        t.IsNew(Key("a")).Should().BeFalse();
        t.Observe(Keys("a", "b", "c")).Should().BeEmpty();
        t.New.Should().BeEquivalentTo(Keys("b", "c"));
    }

    [Fact]
    public void RemovalsAreIgnoredAndAReturningRoleIsNewAgain()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a", "b"));
        t.Observe(Keys("a")).Should().BeEmpty();
        t.Seen.Should().BeEquivalentTo(Keys("a"));
        t.Observe(Keys("a", "b")).Should().Equal(Key("b"));
    }

    [Fact]
    public void MarkerClearsOnTheSecondPanelOpen()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a"));
        t.Observe(Keys("a", "b"));
        t.PanelOpened();
        t.IsNew(Key("b")).Should().BeTrue();
        t.PanelOpened();
        t.IsNew(Key("b")).Should().BeFalse();
        t.ShownOpens.Should().Be(0);
    }

    [Fact]
    public void PanelOpensWithNothingNewDoNotCount()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a"));
        t.PanelOpened();
        t.PanelOpened();
        t.PanelOpened();
        t.Observe(Keys("a", "b"));
        t.PanelOpened();
        t.IsNew(Key("b")).Should().BeTrue();
    }

    [Fact]
    public void EmptyDiscoveryNeverBaselines()
    {
        var t = new NewRoleTracker();
        t.Observe(new HashSet<RoleKey>()).Should().BeEmpty();
        t.Seen.Should().BeEmpty();
        t.Observe(Keys("a")).Should().BeEmpty(); // still the first real sight
    }

    [Fact]
    public void RoundTripsThroughJsonWithSwiftKeys()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a"));
        t.Observe(Keys("a", "b"));
        t.PanelOpened();

        var json = Json.Serialize(t);
        json.Should().Contain("\"seen\":[").And.Contain("\"new\":[").And.Contain("\"shownOpens\":1");
        Json.Deserialize<NewRoleTracker>(json).Should().Be(t);
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a"));
        t.Observe(Keys("a", "b"));
        var copy = t.Clone();
        copy.Should().Be(t);
        copy.PanelOpened();
        copy.PanelOpened();
        t.IsNew(Key("b")).Should().BeTrue();
        copy.IsNew(Key("b")).Should().BeFalse();
    }
}
