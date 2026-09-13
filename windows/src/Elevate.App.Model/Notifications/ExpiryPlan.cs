using System.Text.Json;
using Elevate.Core;
using Elevate.Core.Models;

namespace Elevate.App.Notifications;

/// <summary>
/// One timed toast. Role toasts carry a key and a button (Extend / Activate again); package
/// expiries (<see cref="Key"/> null) are plain. <see cref="Tag"/> is the stable identity used to
/// replace the toast on the next refresh, in the schedule or in the fallback timer alike.
/// </summary>
public sealed record PlannedToast(DateTimeOffset FireAt, string Tag, string Title, string Body, string Button, RoleKey? Key)
{
    public bool IsRoleToast => Key is not null;

    /// <summary>The <c>action</c> argument the button carries; null for a package toast.</summary>
    public string? Action => Key is null ? null : Button == ExpiryPlan.ExtendButton ? ExpiryPlan.ExtendAction : ExpiryPlan.AgainAction;
}

/// <summary>A toast the OS already holds in its schedule, reduced to what the diff needs.</summary>
public sealed record ScheduledEntry(string Tag, DateTimeOffset FireAt);

/// <summary>The changes that bring the OS schedule in line with the wanted set.</summary>
public sealed record ScheduleChanges(IReadOnlyList<string> Remove, IReadOnlyList<PlannedToast> Add);

/// <summary>
/// The platform-free half of the Windows <c>ExpiryNotifier</c>: which toasts an assignment set
/// needs, how an OS schedule is reconciled with them, and how a toast's arguments name a role.
/// Port of the timing in the macOS <c>ExpiryNotifier</c>.
/// </summary>
public static class ExpiryPlan
{
    public const string Group = "elevate-expiry";
    public const string ExtendButton = "Extend";
    public const string AgainButton = "Activate again";
    public const string ExtendAction = "extend";
    public const string AgainAction = "again";
    public const string OpenAction = "open";
    public const string ActionArgument = "action";
    public const string KeyArgument = "key";
    private const string ExpiryPrefix = "expiry-";
    private const string ExpiredPrefix = "expired-";
    private const string PackagePrefix = "package-expired-";

    public static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ExpiredDelay = TimeSpan.FromSeconds(5);

    /// <summary>Toasts nearer than this are not worth scheduling: the OS drops them and the timer would fire at once.</summary>
    public static readonly TimeSpan MinimumLead = TimeSpan.FromSeconds(1);

    /// <summary>Two toasts per active assignment with an end date: "expires in 5 minutes" and "expired".</summary>
    public static IReadOnlyList<PlannedToast> ForRoles(
        IReadOnlyList<ActiveAssignment> assignments,
        IReadOnlyDictionary<RoleKey, string> names,
        IReadOnlyDictionary<TenantKey, string> tenantNames)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(tenantNames);

        var planned = new List<PlannedToast>();
        foreach (var a in assignments.Where(a => a.Status.Kind == AssignmentStatusKind.Active))
        {
            if (a.EndDateTime is not { } end)
            {
                continue;
            }

            var id = a.AssignmentId ?? Guid.NewGuid().ToString("N");
            var name = names.GetValueOrDefault(a.RoleKey) ?? "PIM role";
            var tenant = tenantNames.GetValueOrDefault(a.RoleKey.TenantKey) ?? a.RoleKey.TenantId;
            planned.Add(new PlannedToast(end - LeadTime, ExpiryPrefix + id, $"{name} expires in 5 minutes", tenant, ExtendButton, a.RoleKey));
            planned.Add(new PlannedToast(end + ExpiredDelay, ExpiredPrefix + id, $"{name} expired", tenant, AgainButton, a.RoleKey));
        }

        return planned;
    }

    /// <summary>One "expired" toast per delivered access package assignment, with no button: nothing in Elevate can renew it.</summary>
    public static IReadOnlyList<PlannedToast> ForPackages(IReadOnlyList<PackageExpiry> expiries)
    {
        ArgumentNullException.ThrowIfNull(expiries);
        return expiries
            .Select(e => new PlannedToast(e.At + ExpiredDelay, PackagePrefix + e.Id, $"{e.PackageName} expired", $"Access package in {e.TenantName}", string.Empty, null))
            .ToList();
    }

    /// <summary>The toasts still worth delivering: those more than <see cref="MinimumLead"/> away.</summary>
    public static IReadOnlyList<PlannedToast> Pending(IEnumerable<PlannedToast> planned, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(planned);
        return planned.Where(p => p.FireAt > now + MinimumLead).ToList();
    }

    /// <summary>Whether a tag names one of the role toasts (as opposed to a package expiry or a toast from elsewhere).</summary>
    public static bool IsRoleTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.StartsWith(ExpiryPrefix, StringComparison.Ordinal) || tag.StartsWith(ExpiredPrefix, StringComparison.Ordinal);
    }

    public static bool IsPackageTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.StartsWith(PackagePrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reconciles the OS schedule with <paramref name="wanted"/> within one family of tags: entries
    /// that are no longer wanted or whose time moved (a role that was extended) are removed, wanted
    /// toasts the schedule lacks are added, and matching ones are left alone. Entries outside the
    /// family (<paramref name="owns"/> false) are never touched.
    /// </summary>
    public static ScheduleChanges Reconcile(IEnumerable<ScheduledEntry> existing, IReadOnlyList<PlannedToast> wanted, Func<string, bool> owns)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(owns);

        var wantedByTag = wanted.ToDictionary(p => p.Tag, StringComparer.Ordinal);
        var remove = new List<string>();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in existing.Where(e => owns(e.Tag)))
        {
            if (wantedByTag.TryGetValue(entry.Tag, out var p) && (p.FireAt - entry.FireAt).Duration() < TimeSpan.FromSeconds(1) && kept.Add(entry.Tag))
            {
                continue;
            }

            remove.Add(entry.Tag);
        }

        var add = wanted.Where(p => !kept.Contains(p.Tag)).ToList();
        return new ScheduleChanges(remove, add);
    }

    /// <summary>The <c>key</c> argument value: the role key as the same JSON <c>state.json</c> uses.</summary>
    public static string EncodeKey(RoleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Json.Serialize(key);
    }

    /// <summary>
    /// The role a toast activation names, or null when the arguments are not one of ours: Dismiss,
    /// a missing or malformed key, or an action the app does not act on.
    /// </summary>
    public static RoleKey? RoleFor(IDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.TryGetValue(ActionArgument, out var action) || action is not (ExtendAction or AgainAction or OpenAction))
        {
            return null;
        }

        if (!arguments.TryGetValue(KeyArgument, out var json))
        {
            return null;
        }

        try
        {
            return Json.Deserialize<RoleKey>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
