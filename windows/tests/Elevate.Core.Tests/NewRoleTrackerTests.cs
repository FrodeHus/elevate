using Elevate.Core.Coordination;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>NewRoleTrackerTests</c>.</summary>
public class NewRoleTrackerTests
{
    private static RoleKey Key(string n) => new("i", "t", new EntraDirectoryScope(n, "/"));

    private static HashSet<RoleKey> Keys(params string[] names) => [.. names.Select(Key)];

    /// <summary>Feeds discoveries in order and returns the resulting tracker.</summary>
    private static NewRoleTracker After(params HashSet<RoleKey>[] discoveries) =>
        discoveries.Aggregate(new NewRoleTracker(), (t, d) => t.Observe(d).Next);

    [Fact]
    public void FirstObserveBaselinesWithoutReportingAdditions()
    {
        var (t, added) = new NewRoleTracker().Observe(Keys("a", "b"));
        added.Should().BeEmpty();
        t.Seen.Should().BeEquivalentTo(Keys("a", "b"));
        t.New.Should().BeEmpty();
    }

    [Fact]
    public void AdditionsAreReportedOnceAndMarkedNew()
    {
        var (t, added) = After(Keys("a")).Observe(Keys("a", "c", "b"));
        added.Should().BeEquivalentTo(Keys("b", "c"));
        t.IsNew(Key("b")).Should().BeTrue();
        t.IsNew(Key("c")).Should().BeTrue();
        t.IsNew(Key("a")).Should().BeFalse();
        var (again, addedAgain) = t.Observe(Keys("a", "b", "c"));
        addedAgain.Should().BeEmpty();
        again.New.Should().BeEquivalentTo(Keys("b", "c"));
    }

    [Fact]
    public void RemovalsAreIgnoredAndAReturningRoleIsNewAgain()
    {
        var (t, added) = After(Keys("a", "b")).Observe(Keys("a"));
        added.Should().BeEmpty();
        t.Seen.Should().BeEquivalentTo(Keys("a"));
        t.Observe(Keys("a", "b")).Added.Should().Equal(Key("b"));
    }

    [Fact]
    public void MarkerClearsOnTheSecondPanelOpen()
    {
        var t = After(Keys("a"), Keys("a", "b")).PanelOpened();
        t.IsNew(Key("b")).Should().BeTrue();
        t = t.PanelOpened();
        t.IsNew(Key("b")).Should().BeFalse();
        t.ShownOpens.Should().Be(0);
    }

    [Fact]
    public void PanelOpensWithNothingNewDoNotCount()
    {
        var t = After(Keys("a")).PanelOpened().PanelOpened().PanelOpened();
        t = t.Observe(Keys("a", "b")).Next.PanelOpened();
        t.IsNew(Key("b")).Should().BeTrue();
    }

    [Fact]
    public void OpensCountedAgainstAVanishedMarkerDoNotShortenTheNextOne()
    {
        // b is new, the panel opens once, then b disappears: the marker empties and the count must go with it.
        var t = After(Keys("a"), Keys("a", "b")).PanelOpened();
        t = t.Observe(Keys("a")).Next;
        t.New.Should().BeEmpty();
        t.ShownOpens.Should().Be(0);

        // c gets its full two opens.
        t = t.Observe(Keys("a", "c")).Next.PanelOpened();
        t.IsNew(Key("c")).Should().BeTrue();
        t.PanelOpened().IsNew(Key("c")).Should().BeFalse();
    }

    [Fact]
    public void EmptyDiscoveryNeverBaselines()
    {
        var (t, added) = new NewRoleTracker().Observe(new HashSet<RoleKey>());
        added.Should().BeEmpty();
        t.Seen.Should().BeEmpty();
        t.Observe(Keys("a")).Added.Should().BeEmpty(); // still the first real sight
    }

    [Fact]
    public void ObserveAndPanelOpenedLeaveTheOriginalUntouched()
    {
        var t = After(Keys("a"), Keys("a", "b"));
        t.PanelOpened().PanelOpened();
        t.Observe(Keys("a", "b", "c"));
        t.IsNew(Key("b")).Should().BeTrue();
        t.IsNew(Key("c")).Should().BeFalse();
        t.ShownOpens.Should().Be(0);
    }

    [Fact]
    public void RoundTripsThroughJsonWithSwiftKeys()
    {
        var t = After(Keys("a"), Keys("a", "b")).PanelOpened();

        var json = Json.Serialize(t);
        json.Should().Contain("\"seen\":[").And.Contain("\"new\":[").And.Contain("\"shownOpens\":1");
        Json.Deserialize<NewRoleTracker>(json).Should().Be(t);
    }
}
