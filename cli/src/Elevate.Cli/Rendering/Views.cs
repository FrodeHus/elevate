using Elevate.Cli.Selection;
using Elevate.Cli.Session;
using Elevate.Core.Coordination;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Spectre.Console;

namespace Elevate.Cli.Rendering;

/// <summary>The JSON shapes the read commands print. Stable field names; add, never rename.</summary>
public static class Dto
{
    public sealed record Account(string Id, string Upn, string DisplayName, string HomeTenantId, string SignInMethod, int Tenants, bool NotPermitted);

    public sealed record Tenant(
        string AccountId, string Account, string TenantId, string DisplayName, string Source, string DiscoveryMode,
        IReadOnlyList<string> Flags, string? Error);

    public sealed record Policy(string DefaultDuration, string MaximumDuration, bool RequiresJustification, bool RequiresTicket, bool RequiresMfa, bool RequiresApproval, string? AuthenticationContext);

    public sealed record Assignment(string Status, DateTimeOffset Start, DateTimeOffset? End, string? TimeLeft, string? FailureReason);

    public sealed record Role(
        string Id, string Name, string Kind, string? Detail, string? ViaGroup, string Source, string TenantId, string Tenant,
        string AccountId, string Account, Policy Policy, bool CanActivate, string? ViewOnlyReason, Assignment? Assignment, RoleKey Key);

    public sealed record Outcome(string Id, string Name, string Tenant, string Account, string Result, string? Message, Assignment? Assignment);

    public sealed record Profile(string Id, string Name, string Source, int Roles, string? LastJustification, IReadOnlyList<ProfileEntry> Entries);

    public sealed record ProfileEntry(string RoleId, string Name, string Kind, string Tenant, string Account, string? LastDuration, RoleKey Key);

    /// <summary>The design §7.1 document <c>profiles export</c> prints; field names are the format's.</summary>
    public sealed record ExportedProfileSet(int Version, IReadOnlyList<ExportedProfile> Profiles);

    public sealed record ExportedProfile(string Id, string Name, string? Reason, bool Pinned, IReadOnlyList<ExportedRole> Roles);

    public sealed record ExportedRole(
        string Kind, string Tenant, string? Role, string? Scope, string? DirectoryScope, string? Group, string? Access, string? Duration);

    public sealed record PlanItem(string RoleId, string Name, string Tenant, string Account, string Disposition, string Duration);

    public sealed record Approval(
        string Id, string RequestId, string Requester, string Target, string Kind, string Action, string? Scope, string TenantId, string Tenant,
        string Account, string? Duration, DateTimeOffset? RequestedAt, string? Justification, bool Decidable);

    public sealed record CatalogueEntry(string TemplateId, string DisplayName, string Description, bool IsPrivileged);

    public sealed record Package(
        string Id, string PackageId, string Name, string? Description, bool Hidden, string TenantId, string Tenant, string Account,
        string? State, string? RequestId, string? AssignmentId);

    public sealed record PackageRequest(
        string Id, string RequestId, string PackageId, string Package, string State, string? Status, string? Justification,
        DateTimeOffset? RequestedAt, DateTimeOffset? CompletedAt, string? PolicyId, string TenantId, string Tenant, string Account, bool Cancellable);

    public sealed record PackageAssignment(
        string Id, string AssignmentId, string PackageId, string Package, string State, string? Policy, DateTimeOffset? ExpiresAt, string? ExpiresIn,
        string TenantId, string Tenant, string Account);

    public sealed record PolicyOption(string Id, string Name, string? Description, bool RequiresApproval, bool RequiresAnswers);
}

/// <summary>Turns session objects into DTOs and Spectre tables. Shared by the commands and the watch loop.</summary>
public static class Views
{
    public static string Iso(TimeSpan d) => Iso8601Duration.Format(d);

    public static string KindName(RoleScopeKind kind) => kind switch
    {
        RoleScopeKind.EntraDirectory => "entra",
        RoleScopeKind.AzureResource => "azure",
        _ => "group",
    };

    public static string StatusName(AssignmentStatusKind kind) => kind switch
    {
        AssignmentStatusKind.Active => "active",
        AssignmentStatusKind.PendingApproval => "pendingApproval",
        AssignmentStatusKind.PendingProvisioning => "pendingProvisioning",
        AssignmentStatusKind.Scheduled => "scheduled",
        _ => "failed",
    };

    public static Dto.Assignment? Assignment(ActiveAssignment? a, DateTimeOffset now)
    {
        if (a is null)
        {
            return null;
        }

        var left = a.Status.Kind == AssignmentStatusKind.Active && a.EndDateTime is { } end ? Countdown.Remaining(end, now) : null;
        return new Dto.Assignment(StatusName(a.Status.Kind), a.StartDateTime, a.EndDateTime, left is { } l ? Countdown.Label(l) : null, a.Status.FailureReason);
    }

    public static Dto.Policy Policy(RolePolicy p) => new(
        Iso(p.DefaultDuration), Iso(p.MaximumDuration), p.RequiresJustification, p.RequiresTicket, p.RequiresMfa, p.RequiresApproval, p.AuthenticationContext);

    public static Dto.Role Role(ElevateSession session, EligibleRole role, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(role);
        var key = role.Key;
        return new Dto.Role(
            ShortId.For(key), role.DisplayName, KindName(key.Scope.Kind), role.Detail, role.ViaGroup,
            role.Source == RoleSource.Manual ? "manual" : "discovered",
            key.TenantId, session.TenantName(key.TenantKey), key.IdentityId, session.AccountName(key.IdentityId),
            Policy(role.Policy), session.CanActivate(key),
            key.Scope.Kind == RoleScopeKind.EntraDirectory ? session.EntraViewOnlyReason(key.TenantKey) : null,
            Assignment(session.Active.GetValueOrDefault(key), now), key);
    }

    public static Dto.Account Account(ElevateSession session, Identity identity)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(identity);
        return new Dto.Account(identity.Id, identity.Upn, identity.DisplayName, identity.HomeTenantId, identity.SignInMethod.StorageKey,
            session.Tenants.Count(t => t.IdentityId == identity.Id), !session.IsMethodAllowed(identity.SignInMethod));
    }

    public static IReadOnlyList<string> TenantFlags(ElevateSession session, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tenant);
        var flags = new List<string>();
        if (session.Identity(tenant.IdentityId)?.SignInMethod.IsPreauthorisedForEntraActivation == false)
        {
            flags.Add("azure roles only");
        }

        if (tenant.DiscoveryMode == DiscoveryMode.ManualRoles)
        {
            flags.Add("manual roles");
        }

        if (tenant.AzureUnavailableReason is not null)
        {
            flags.Add("azure off");
        }

        if (tenant.GroupsUnavailableReason is not null)
        {
            flags.Add("groups off");
        }

        if (tenant.EntraActivation is { IsSupported: false })
        {
            flags.Add("entra view-only");
        }

        if (session.IsPinnedTenant(tenant.Key))
        {
            flags.Add("pinned");
        }

        return flags;
    }

    public static Dto.Tenant Tenant(ElevateSession session, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tenant);
        return new Dto.Tenant(
            tenant.IdentityId, session.AccountName(tenant.IdentityId), tenant.TenantId, tenant.DisplayName,
            tenant.Source.ToString().ToLowerInvariant(), tenant.DiscoveryMode == DiscoveryMode.Automatic ? "automatic" : "manualRoles",
            TenantFlags(session, tenant), session.TenantErrors.GetValueOrDefault(tenant.Key) ?? tenant.LastDiscoveryError);
    }

    public static Dto.Profile Profile(ElevateSession session, ActivationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(profile);
        return new Dto.Profile(
            profile.Id.ToString("D"), profile.Name, ProfileSourceName(profile), profile.Entries.Count, profile.LastJustification,
            [.. profile.Entries.Select(e => new Dto.ProfileEntry(
                ShortId.For(e.RoleKey), session.RoleName(e.RoleKey), KindName(e.RoleKey.Scope.Kind),
                session.TenantName(e.RoleKey.TenantKey), session.AccountName(e.RoleKey.IdentityId),
                e.LastDuration is { } d ? Iso(d) : null, e.RoleKey))]);
    }

    /// <summary>Where a profile came from, as <c>profiles list</c> and <c>--json</c> name it.</summary>
    public static string ProfileSourceName(ActivationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Source == ProfileSource.Managed ? "managed" : "user";
    }

    /// <summary>
    /// One user profile as the document an administrator publishes (design §7.1): roles named from
    /// what is loaded, falling back to the ids in the key, and tenants as tenant ids.
    /// </summary>
    public static Dto.ExportedProfileSet ExportedSet(ElevateSession session, ActivationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(profile);
        return new Dto.ExportedProfileSet(ManagedProfileSet.Version,
        [
            new Dto.ExportedProfile(Slug(profile.Name), profile.Name, profile.LastJustification, profile.Pinned,
                [.. profile.Entries.Select(e => ExportedRole(session, e))]),
        ]);
    }

    private static Dto.ExportedRole ExportedRole(ElevateSession session, ActivationProfile.Entry entry)
    {
        var key = entry.RoleKey;
        var name = session.LoadedRoleName(key);
        var duration = entry.LastDuration is { } d ? Iso(d) : null;
        return key.Scope switch
        {
            EntraDirectoryScope entra => new("entraDirectory", key.TenantId, name ?? entra.RoleDefinitionId, null,
                entra.DirectoryScopeId == "/" ? null : entra.DirectoryScopeId, null, null, duration),
            AzureResourceScope azure => new("azureResource", key.TenantId, name ?? azure.RoleDefinitionId, azure.Scope,
                null, null, null, duration),
            GroupScope group => new("group", key.TenantId, null, null, null, name ?? group.GroupId,
                group.AccessId == GroupAccess.Owner ? "owner" : "member", duration),
            _ => new("group", key.TenantId, null, null, null, name ?? "unknown", "member", duration),
        };
    }

    /// <summary>The profile's name as a document id: lower case, runs of anything else a dash.</summary>
    public static string Slug(string name)
    {
        var slug = new string([.. (name ?? string.Empty).ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        slug = slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
        return slug.Length == 0 ? "profile" : slug;
    }

    public static Dto.Approval Approval(ElevateSession session, ApprovalRequest r)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(r);
        return new Dto.Approval(
            ShortId.For(r), r.Id, r.RequesterName, r.TargetName, KindName(r.Kind), r.Action.ToString().ToLowerInvariant(), r.ScopeCaption,
            r.TenantKey.TenantId, session.TenantName(r.TenantKey), session.AccountName(r.TenantKey.IdentityId),
            r.RequestedDuration is { } d ? Iso(d) : null, r.CreatedAt, r.Justification, r.Action == ApprovalAction.Activate);
    }

    public static Dto.Outcome Outcome(ElevateSession session, ActivationOutcome outcome, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(outcome);
        var (result, message) = outcome.Result switch
        {
            ActivationResult.Activated => ("activated", null),
            ActivationResult.PendingApproval => ("pendingApproval", "Awaiting approval"),
            ActivationResult.Scheduled s => ("scheduled", $"Starts in {Countdown.Until(s.Assignment.StartDateTime, now)}"),
            ActivationResult.Failed f => ("failed", f.Error.UserMessage),
            _ => ("unknown", (string?)null),
        };
        return new Dto.Outcome(ShortId.For(outcome.RoleKey), session.RoleName(outcome.RoleKey), session.TenantName(outcome.RoleKey.TenantKey),
            session.AccountName(outcome.RoleKey.IdentityId), result, message, Assignment(ElevateSession.AssignmentOf(outcome.Result), now));
    }

    // MARK: Tables

    public static string StatusMarkup(ActiveAssignment? a, DateTimeOffset now)
    {
        if (a is null)
        {
            return "[grey]eligible[/]";
        }

        switch (a.Status.Kind)
        {
            case AssignmentStatusKind.Active:
                if (a.EndDateTime is { } end && Countdown.Remaining(end, now) is { } left)
                {
                    var color = left <= TimeSpan.FromMinutes(5) ? "yellow" : "green";
                    return $"[{color}]active[/] [grey]{Markup.Escape(Countdown.Label(left))} left[/]";
                }

                return "[green]active[/]";
            case AssignmentStatusKind.PendingApproval:
                return "[yellow]awaiting approval[/]";
            case AssignmentStatusKind.PendingProvisioning:
                return "[yellow]provisioning[/]";
            case AssignmentStatusKind.Scheduled:
                return $"[blue]scheduled[/] [grey]starts in {Markup.Escape(Countdown.Until(a.StartDateTime, now))}[/]";
            default:
                return $"[red]failed[/] [grey]{Markup.Escape(a.Status.FailureReason ?? string.Empty)}[/]";
        }
    }

    public static string PolicyMarkup(EligibleRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        var parts = new List<string> { Countdown.Label(role.Policy.DefaultDuration) };
        var notes = PolicyNotes.Caption(role.Policy);
        if (notes is not null)
        {
            parts.Add(notes);
        }

        if (role.Policy.RequiresTicket)
        {
            parts.Add("ticket");
        }

        return Markup.Escape(string.Join(" · ", parts));
    }

    public static Table RolesTable(ElevateSession session, IEnumerable<EligibleRole> roles, DateTimeOffset now, bool showAccount)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(roles);
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[grey]ID[/]");
        table.AddColumn("Role");
        table.AddColumn("Kind");
        table.AddColumn("Scope");
        table.AddColumn("Tenant");
        if (showAccount)
        {
            table.AddColumn("Account");
        }

        table.AddColumn("Policy");
        table.AddColumn("Status");
        foreach (var role in roles)
        {
            var key = role.Key;
            var name = Markup.Escape(role.DisplayName);
            if (role.ViaGroup is { } via)
            {
                name += $" [grey]via {Markup.Escape(via)}[/]";
            }

            if (!session.CanActivate(key))
            {
                name += " [grey](view only)[/]";
            }

            var cells = new List<string>
            {
                $"[grey]{ShortId.For(key)}[/]",
                name,
                RoleSelector.KindLabel(key.Scope.Kind),
                Markup.Escape(role.Detail ?? "—"),
                Markup.Escape(session.TenantName(key.TenantKey)),
            };
            if (showAccount)
            {
                cells.Add(Markup.Escape(session.AccountName(key.IdentityId)));
            }

            cells.Add(PolicyMarkup(role));
            cells.Add(StatusMarkup(session.Active.GetValueOrDefault(key), now));
            table.AddRow(cells.ToArray());
        }

        return table;
    }

    public static Table ActiveTable(ElevateSession session, IEnumerable<ActiveAssignment> assignments, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(assignments);
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[grey]ID[/]");
        table.AddColumn("Role");
        table.AddColumn("Kind");
        table.AddColumn("Tenant");
        table.AddColumn("Account");
        table.AddColumn("Status");
        table.AddColumn("Ends");
        foreach (var a in ActiveSummary.Order(assignments))
        {
            var key = a.RoleKey;
            var ends = a.Status.Kind == AssignmentStatusKind.Active && a.EndDateTime is { } end
                ? end.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : a.Status.Kind == AssignmentStatusKind.Scheduled
                    ? "starts " + a.StartDateTime.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                    : "—";
            table.AddRow(
                $"[grey]{ShortId.For(key)}[/]",
                Markup.Escape(session.RoleName(key)),
                RoleSelector.KindLabel(key.Scope.Kind),
                Markup.Escape(session.TenantName(key.TenantKey)),
                Markup.Escape(session.AccountName(key.IdentityId)),
                StatusMarkup(a, now),
                Markup.Escape(ends));
        }

        return table;
    }

    public static Table OutcomesTable(ElevateSession session, IEnumerable<ActivationOutcome> outcomes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(outcomes);
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Role");
        table.AddColumn("Tenant");
        table.AddColumn("Result");
        foreach (var o in outcomes)
        {
            var result = o.Result switch
            {
                ActivationResult.Activated a when a.Assignment.EndDateTime is { } end =>
                    $"[green]activated[/] [grey]until {Markup.Escape(end.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture))} ({Markup.Escape(Countdown.Label(end - now))})[/]",
                ActivationResult.Activated => "[green]activated[/]",
                ActivationResult.PendingApproval => "[yellow]awaiting approval[/]",
                ActivationResult.Scheduled s => $"[blue]scheduled[/] [grey]starts in {Markup.Escape(Countdown.Until(s.Assignment.StartDateTime, now))}[/]",
                ActivationResult.Failed f => $"[red]failed[/] {Markup.Escape(f.Error.UserMessage)}",
                _ => "?",
            };
            table.AddRow(Markup.Escape(session.RoleName(o.RoleKey)), Markup.Escape(session.TenantName(o.RoleKey.TenantKey)), result);
        }

        return table;
    }

    public static Table ApprovalsTable(ElevateSession session, IEnumerable<ApprovalRequest> requests, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(requests);
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[grey]ID[/]");
        table.AddColumn("Requester");
        table.AddColumn("Role / group");
        table.AddColumn("Tenant");
        table.AddColumn("Duration");
        table.AddColumn("Requested");
        table.AddColumn("Reason");
        foreach (var r in requests)
        {
            var target = Markup.Escape(r.TargetName);
            if (r.ScopeCaption is { } caption)
            {
                target += $" [grey]{Markup.Escape(caption)}[/]";
            }

            if (r.Action != ApprovalAction.Activate)
            {
                target += " [yellow](decide in the portal)[/]";
            }

            table.AddRow(
                $"[grey]{ShortId.For(r)}[/]",
                Markup.Escape(r.RequesterName),
                target,
                Markup.Escape(session.TenantName(r.TenantKey)),
                r.RequestedDuration is { } d ? Markup.Escape(Countdown.Label(d)) : "—",
                r.CreatedAt is { } at ? Markup.Escape(Countdown.Label(now - at) + " ago") : "—",
                Markup.Escape(r.Justification ?? "—"));
        }

        return table;
    }

    // MARK: Access packages

    public static string RequestStateName(AccessPackageRequestState state) => state switch
    {
        AccessPackageRequestState.Submitted => "submitted",
        AccessPackageRequestState.PendingApproval => "pendingApproval",
        AccessPackageRequestState.Delivering => "delivering",
        AccessPackageRequestState.Delivered => "delivered",
        AccessPackageRequestState.DeliveryFailed => "deliveryFailed",
        AccessPackageRequestState.Denied => "denied",
        AccessPackageRequestState.Scheduled => "scheduled",
        AccessPackageRequestState.Canceled => "canceled",
        AccessPackageRequestState.PartiallyDelivered => "partiallyDelivered",
        _ => "unknown",
    };

    public static string AssignmentStateName(AccessPackageAssignmentState state) => state switch
    {
        AccessPackageAssignmentState.Delivering => "delivering",
        AccessPackageAssignmentState.Delivered => "delivered",
        AccessPackageAssignmentState.Expired => "expired",
        _ => "unknown",
    };

    /// <summary>The state caption for a requestable package: a delivered assignment wins, then the newest open request.</summary>
    public static (string? Text, string? RequestId, string? AssignmentId) PackageState(TenantPackages read, string packageId)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read.Assignments.FirstOrDefault(a => a.PackageId == packageId && a.State == AccessPackageAssignmentState.Delivered) is { } delivered)
        {
            return ("delivered", null, delivered.Id);
        }

        var open = read.Requests
            .Where(r => r.PackageId == packageId && r.State.IsOpen())
            .OrderByDescending(r => r.CreatedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        return open is null ? (null, null, null) : (RequestStateName(open.State), open.Id, null);
    }

    public static Dto.Package Package(ElevateSession session, TenantPackages read, AccessPackage package)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(package);
        var (state, requestId, assignmentId) = PackageState(read, package.Id);
        return new Dto.Package(ShortId.For(read.Key, package.Id), package.Id, package.DisplayName, package.Description, package.IsHidden,
            read.Key.TenantId, session.TenantName(read.Key), session.AccountName(read.Key.IdentityId), state, requestId, assignmentId);
    }

    public static Dto.PackageRequest PackageRequest(ElevateSession session, TenantKey key, AccessPackageRequest r)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(r);
        return new Dto.PackageRequest(ShortId.For(key, r.Id), r.Id, r.PackageId, r.PackageName, RequestStateName(r.State), r.Status, r.Justification,
            r.CreatedAt, r.CompletedAt, r.PolicyId, key.TenantId, session.TenantName(key), session.AccountName(key.IdentityId), r.State.IsCancellable());
    }

    public static Dto.PackageAssignment PackageAssignment(ElevateSession session, TenantKey key, AccessPackageAssignment a, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(a);
        var left = a.ExpiresAt is { } end && end > now ? Countdown.Label(end - now) : null;
        return new Dto.PackageAssignment(ShortId.For(key, a.Id), a.Id, a.PackageId, a.PackageName, AssignmentStateName(a.State), a.PolicyName, a.ExpiresAt, left,
            key.TenantId, session.TenantName(key), session.AccountName(key.IdentityId));
    }

    public static Dto.PolicyOption PolicyOption(PolicyRequirement p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new Dto.PolicyOption(p.Id, p.DisplayName, p.Description, p.IsApprovalRequired, p.RequiresAnswers);
    }

    public static string RequestStateMarkup(AccessPackageRequestState state) => state switch
    {
        AccessPackageRequestState.Submitted => "[yellow]submitted[/]",
        AccessPackageRequestState.PendingApproval => "[yellow]awaiting approval[/]",
        AccessPackageRequestState.Delivering => "[yellow]delivering[/]",
        AccessPackageRequestState.Scheduled => "[yellow]scheduled[/]",
        AccessPackageRequestState.PartiallyDelivered => "[yellow]partially delivered[/]",
        AccessPackageRequestState.Delivered => "[green]delivered[/]",
        AccessPackageRequestState.Denied => "[red]denied[/]",
        AccessPackageRequestState.DeliveryFailed => "[red]delivery failed[/]",
        AccessPackageRequestState.Canceled => "[grey]canceled[/]",
        _ => "[grey]unknown[/]",
    };

    private static string LocalDate(DateTimeOffset? date) =>
        date is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "—";

    public static Table PackagesTable(ElevateSession session, IReadOnlyList<TenantPackages> reads, bool showAccount)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reads);
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[grey]ID[/]");
        table.AddColumn("Package");
        table.AddColumn("Description");
        table.AddColumn("Tenant");
        if (showAccount)
        {
            table.AddColumn("Account");
        }

        table.AddColumn("State");
        foreach (var read in reads)
        {
            foreach (var p in read.Packages.OrderBy(p => p.DisplayName, StringComparer.Ordinal))
            {
                var (state, _, _) = PackageState(read, p.Id);
                var name = Markup.Escape(p.DisplayName);
                if (p.IsHidden)
                {
                    name += " [grey](hidden)[/]";
                }

                var cells = new List<string> { $"[grey]{ShortId.For(read.Key, p.Id)}[/]", name, Markup.Escape(p.Description ?? "—"), Markup.Escape(session.TenantName(read.Key)) };
                if (showAccount)
                {
                    cells.Add(Markup.Escape(session.AccountName(read.Key.IdentityId)));
                }

                cells.Add(state switch
                {
                    null => "[grey]—[/]",
                    "delivered" => "[green]delivered[/]",
                    _ => RequestStateMarkup(AccessPackageRequestStates.Parse(state)),
                });
                table.AddRow(cells.ToArray());
            }
        }

        return table;
    }

    public static Table PackageRequestsTable(ElevateSession session, IEnumerable<(TenantKey Key, AccessPackageRequest Request)> requests, DateTimeOffset now, bool all, bool showAccount)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(requests);
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[grey]ID[/]");
        table.AddColumn("Package");
        table.AddColumn("Tenant");
        if (showAccount)
        {
            table.AddColumn("Account");
        }

        table.AddColumn("State");
        table.AddColumn("Requested");
        if (all)
        {
            table.AddColumn("Completed");
        }

        table.AddColumn("Reason");
        foreach (var (key, r) in requests)
        {
            var state = RequestStateMarkup(r.State);
            if (all && r.State is AccessPackageRequestState.DeliveryFailed or AccessPackageRequestState.Canceled && !string.IsNullOrWhiteSpace(r.Status))
            {
                state += $" [grey]{Markup.Escape(r.Status)}[/]";
            }

            var cells = new List<string> { $"[grey]{ShortId.For(key, r.Id)}[/]", Markup.Escape(r.PackageName), Markup.Escape(session.TenantName(key)) };
            if (showAccount)
            {
                cells.Add(Markup.Escape(session.AccountName(key.IdentityId)));
            }

            cells.Add(state);
            cells.Add(r.CreatedAt is { } at ? Markup.Escape(Countdown.Label(now - at) + " ago") : "—");
            if (all)
            {
                cells.Add(Markup.Escape(LocalDate(r.CompletedAt)));
            }

            cells.Add(Markup.Escape(r.Justification ?? "—"));
            table.AddRow(cells.ToArray());
        }

        return table;
    }

    public static Table PackageAssignmentsTable(ElevateSession session, IEnumerable<(TenantKey Key, AccessPackageAssignment Assignment)> assignments, DateTimeOffset now, bool showAccount)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(assignments);
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[grey]ID[/]");
        table.AddColumn("Package");
        table.AddColumn("Tenant");
        if (showAccount)
        {
            table.AddColumn("Account");
        }

        table.AddColumn("Policy");
        table.AddColumn("Expires");
        foreach (var (key, a) in assignments)
        {
            string expires;
            if (a.ExpiresAt is { } end)
            {
                var left = end - now;
                var color = left <= TimeSpan.FromDays(7) ? "orange1" : "grey";
                expires = left > TimeSpan.Zero
                    ? $"{Markup.Escape(LocalDate(end))} [{color}]in {Markup.Escape(Countdown.Label(left))}[/]"
                    : $"{Markup.Escape(LocalDate(end))} [red]expired[/]";
            }
            else
            {
                expires = "[grey]No expiry[/]";
            }

            var cells = new List<string> { $"[grey]{ShortId.For(key, a.Id)}[/]", Markup.Escape(a.PackageName), Markup.Escape(session.TenantName(key)) };
            if (showAccount)
            {
                cells.Add(Markup.Escape(session.AccountName(key.IdentityId)));
            }

            cells.Add(Markup.Escape(a.PolicyName ?? "—"));
            cells.Add(expires);
            table.AddRow(cells.ToArray());
        }

        return table;
    }
}
