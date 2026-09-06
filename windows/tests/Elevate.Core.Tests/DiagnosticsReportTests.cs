using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>DiagnosticsReportTests</c> and <c>ErrorLogTests</c>.</summary>
public class DiagnosticsReportTests
{
    private static DiagnosticsInput MakeInput(
        IReadOnlyList<DiagnosticsError>? errors = null,
        IReadOnlyList<DiagnosticsAccount>? accounts = null,
        IReadOnlyList<DiagnosticsTenant>? tenants = null,
        IReadOnlyList<string>? profiles = null,
        string? hotKey = null) =>
        new("1.2.3", "45", "Unsigned", "Windows 11 26200", accounts ?? [], tenants ?? [], profiles ?? [], hotKey, errors ?? []);

    [Fact]
    public void HeaderLinesPresent()
    {
        var text = DiagnosticsReport.Render(MakeInput());
        text.Should().Contain("1.2.3").And.Contain("45").And.Contain("Unsigned").And.Contain("Windows 11 26200");
    }

    [Fact]
    public void AccountsSectionRendered()
    {
        var text = DiagnosticsReport.Render(MakeInput(accounts: [new("alice@contoso.com", "MSAL", 2)]));
        text.Should().Contain("alice@contoso.com").And.Contain("MSAL").And.Contain("2 tenant(s)");
    }

    [Fact]
    public void TenantsSectionRendered()
    {
        var text = DiagnosticsReport.Render(MakeInput(tenants: [new("Contoso", "tenant-id-1", "auto", ["azure", "groups"])]));
        text.Should().Contain("Contoso").And.Contain("tenant-id-1").And.Contain("auto").And.Contain("azure, groups");
    }

    [Fact]
    public void ProfilesAndHotKeyRendered()
    {
        var text = DiagnosticsReport.Render(MakeInput(profiles: ["Default", "Work"], hotKey: "Ctrl+Shift+E"));
        text.Should().Contain("Default").And.Contain("Work").And.Contain("Hot key: Ctrl+Shift+E");
    }

    [Fact]
    public void NoHotKeyRendersPlaceholder() =>
        DiagnosticsReport.Render(MakeInput()).Should().Contain("Hot key: None");

    [Fact]
    public void ErrorsRenderedWithIso8601UtcTimestamps()
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var text = DiagnosticsReport.Render(MakeInput(errors: [new(date, "boom")]));
        text.Should().Contain("[2023-11-14T22:13:20Z] boom");
    }

    [Fact]
    public void ErrorMessageWithSecretIsRenderedVerbatim() =>
        DiagnosticsReport.Render(MakeInput(errors: [new(DateTimeOffset.UtcNow, "auth failed token=abc123")]))
            .Should().Contain("token=abc123");

    [Fact]
    public void RendererNeverContainsUnpassedClientId() =>
        // The input type has no field for a client id / secret at all, so a client-id-looking
        // string that was never supplied cannot appear.
        DiagnosticsReport.Render(MakeInput()).Should().NotContain("9d3a7e2c-4b1f-4a6e-9c2d-5f8b1c0a7e3d");

    [Fact]
    public void MultipleErrorsRenderedInOrder()
    {
        var text = DiagnosticsReport.Render(MakeInput(errors:
        [
            new(DateTimeOffset.FromUnixTimeSeconds(100), "first error"),
            new(DateTimeOffset.FromUnixTimeSeconds(200), "second error"),
        ]));
        text.IndexOf("first error", StringComparison.Ordinal).Should().BePositive()
            .And.BeLessThan(text.IndexOf("second error", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptySectionsSayNone()
    {
        var text = DiagnosticsReport.Render(MakeInput());
        text.Should().Contain("Accounts:\n  None").And.Contain("Tenants:\n  None")
            .And.Contain("Profiles:\n  None").And.Contain("Recent errors:\n  None");
    }

    [Fact]
    public void ErrorLogAppendsAndReturnsInOldestToNewestOrder()
    {
        var log = new ErrorLog();
        var d1 = DateTimeOffset.FromUnixTimeSeconds(1);
        var d2 = DateTimeOffset.FromUnixTimeSeconds(2);
        log.Append("first", d1);
        log.Append("second", d2);
        log.Entries.Select(e => e.Message).Should().Equal("first", "second");
        log.Entries.Select(e => e.Date).Should().Equal(d1, d2);
    }

    [Fact]
    public void ErrorLogCapsAtCapacityKeepingNewest()
    {
        var log = new ErrorLog(capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            log.Append($"msg{i}", DateTimeOffset.FromUnixTimeSeconds(i));
        }

        log.Entries.Select(e => e.Message).Should().Equal("msg2", "msg3", "msg4");
    }

    [Fact]
    public void ErrorLogDefaultCapacityIs50()
    {
        var log = new ErrorLog();
        for (var i = 0; i < 60; i++)
        {
            log.Append($"msg{i}", DateTimeOffset.FromUnixTimeSeconds(i));
        }

        log.Entries.Should().HaveCount(50);
        log.Entries[0].Message.Should().Be("msg10");
        log.Entries[^1].Message.Should().Be("msg59");
    }

    [Fact]
    public void ErrorLogContentEquality()
    {
        var a = new ErrorLog(capacity: 5);
        var b = new ErrorLog(capacity: 5);
        var d = DateTimeOffset.FromUnixTimeSeconds(10);
        a.Append("x", d);
        b.Append("x", d);
        a.ContentEquals(b).Should().BeTrue();
        b.Append("y", d);
        a.ContentEquals(b).Should().BeFalse();
    }
}
