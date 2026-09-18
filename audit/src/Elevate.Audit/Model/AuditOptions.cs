namespace Elevate.Audit.Model;

/// <summary>Defaults shared by the options record, the command line and the report.</summary>
public static class AuditDefaults
{
    /// <summary>
    /// Days without an activation before an eligibility counts as dormant, and the lookback the activation
    /// history is read over — the two are the same number, so "never activated in the window" and "dormant"
    /// partition the same span of time between them.
    /// </summary>
    public const int DormantAfterDays = 90;
}

/// <summary>The command-line choices the rules and renderers need to know about.</summary>
public sealed record AuditOptions(
    bool AllRoles = false,
    IReadOnlyList<string>? Ignored = null,
    Severity MinSeverity = Severity.Info,
    bool SkipAzure = false,
    int DormantAfterDays = AuditDefaults.DormantAfterDays)
{
    public IReadOnlyList<string> IgnoredRules => Ignored ?? [];

    /// <summary>
    /// How far back the activation history is read: twice the dormancy threshold, so that an activation
    /// can be both inside the window and older than <see cref="DormantAfterDays"/>. A window equal to the
    /// threshold could never produce a dormant eligibility — everything it saw would be recent by
    /// definition. What the tenant actually retains may be less; the report states the window it asked for
    /// and says so.
    /// </summary>
    public int LookbackDays => DormantAfterDays * 2;
}
