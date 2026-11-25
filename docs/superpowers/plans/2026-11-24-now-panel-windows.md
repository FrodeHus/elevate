# Now Panel (Windows) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the Windows tray flyout as a sidebar (pinned profiles, accounts and tenants) beside a main view that shows Home, one tenant, the role list or one profile.

**Architecture:** Every decision lives in the `AppModel` partial class in `Elevate.App.Model` as plain values (`PanelSelection`, `PanelContent`, `TenantMark`, a `ProfileRunSession` per profile) with xUnit tests in `Elevate.App.Tests`. `PanelListBuilder` in `Elevate.App` turns `AppModel.PanelContent` into list groups as it does today; a new `PanelSidebar` control and a `RunProfileContent` control (shared by `RunProfileWindow` and the flyout) are drawn from the model in code-behind, following `PanelView.Draw`.

**Tech Stack:** C# / .NET 10, WinUI 3, xUnit 2.9 + FluentAssertions. `Elevate.App.Model`, `Elevate.App` and `Elevate.App.Tests` build and test on Windows 11 only (on a Mac, use the `windows-vm` skill).

**Spec:** `docs/superpowers/specs/2026-11-24-elevate-now-panel-design.md`. This is the port of `2026-11-24-now-panel-macos.md`; the two ship in the same release. All paths below are relative to `windows/`; `VM/` is `src/Elevate.App.Model/ViewModels/`.

## Global Constraints

- **Frame:** flyout width 516 (sidebar 136 + main 380); height at most 720. Today's 380 and 640 are set in three places: `Shell/FlyoutWindow.xaml.cs:23-24`, `Shell/FlyoutWindow.xaml:7`, `Views/PanelView.xaml:7`.
- **Copy, verbatim:** `All tenants`, `Profiles`, `Tenants`, `All N`, `Add account…`, `Sign in`, `Approvals`, `Active now`, `Browse all roles`, `Nothing active`, `You hold no elevated access right now.`, `Eligible`, `Filter roles`, `Filter roles in <tenant>`, `N more in other tenants`, `Group membership`, `Group ownership`, `Access packages…`, `Activate N`, tab titles `All`, `Entra`, `Azure`, `Groups`.
- **Ctrl-click** is the silent gesture wherever macOS uses Option-click.
- **Nothing activates on a plain click** that asks first today.
- **No new windows.** Activation, decision, save-profile, access-package, Settings and add-account windows stay as they are.
- **Fluent controls and theme resources**, not the mockups' hex values.
- **Warnings are errors** (`Directory.Build.props`). Tests use `TestModel` and `Sample` from `tests/Elevate.App.Tests/Support/TestModel.cs`.
- **Branch:** `now-panel`, the same branch as the macOS plan. Every commit builds and passes `dotnet test Elevate.sln`.

Run one test class with (from `windows/`):

```bash
dotnet test Elevate.sln --filter "FullyQualifiedName~<ClassName>"
```

## Review Focus

1. **The scoped tenant disappears** (removed, account signed out or needing sign-in) while stored in `settings.json`: the flyout shows All tenants. Test in Task 2.
2. **The shown profile is deleted or a managed profile is withdrawn** while its view or draft is up: selection clears, session dropped. Test in Task 4.
3. **The flyout hides mid-run** (it hides on deactivation; MFA opens the browser): the run finishes, the toast is posted, the next open lands on the results. Test in Task 4.
4. **A filter on a tenant view matches nothing there:** `No matches`, plus `N more in other tenants` when other tenants match. Test in Task 2.
5. **Many accounts and tenants with long names, and 150% and 200% display scaling:** the sidebar scrolls under the fixed Profiles section, names trim with a tooltip, and `FlyoutWindow.PixelSize()` yields 516 × scale wide and never taller than 720 × scale. Manual check in Task 6.

---

### Task 1: The All tab

**Files:**
- Modify: `src/Elevate.App.Model/Services/AppSettings.cs:11,47,354` (`PanelTab`, default)
- Modify: `VM/AppModel.Panel.cs` (`Title`, `Kinds`, new members)
- Modify: `src/Elevate.App/Views/PanelItems.cs:933` (`AddTenantRows`), `:1012` (`RoleRowFor`), `src/Elevate.App/Views/Widgets.cs:164` (`SegmentedPivots`)
- Test: `tests/Elevate.App.Tests/AppModelPanelTests.cs`

**Interfaces:**
- Produces: `PanelTab.All` (declared first); `AppModel.Title(PanelTab.All) == "All"`; `Kinds(PanelTab.All)` is all three kinds; `IReadOnlyList<RoleKey> AppModel.ListRows(TenantKey tenantKey)`, one tenant's role rows in display order for the current tab; `string? AppModel.GroupCaption(RoleKey key)`.

- [ ] **Step 1: Write the failing tests** in `AppModelPanelTests`, with a model holding one Entra, one Azure and one group role in one tenant (as the existing tab tests build it):

```csharp
[Fact] public async Task AllTabShowsEveryKindInOrder()
// model.PanelTab = PanelTab.All;
// model.RolesFor(tenantKey, PanelTab.All).Should().HaveCount(3);
// model.ListRows(tenantKey).Should().Equal(entraKey, azureKey, groupKey);

[Fact] public void TabDefaultsToAllWhenNothingIsStored()
// a fresh AppSettings over an empty file: settings.PanelTab.Should().Be(PanelTab.All);

[Fact] public void AStoredTabIsKept()
// settings.json containing {"panelTab":"roles"} loads as PanelTab.Roles

[Fact] public async Task GroupCaptionNamesMembershipAndOwnership()
// member key → "Group membership"; owner key → "Group ownership"; Entra key → null
```

- [ ] **Step 2: Run and see them fail** (`--filter "FullyQualifiedName~AppModelPanelTests"`).
- [ ] **Step 3: Implement.** `ListRows` on `All`: Entra keys, then the keys of `ScopeTree.Flatten(AzureTree(tenantKey), …)` with the collapse test the Azure tab uses, then group keys.
- [ ] **Step 4: Render it.** `AddTenantRows` on `All` adds the Entra rows, then `AddScopeTreeRows`, then the group rows. `RoleRowFor` sets the caption from `GroupCaption` only when `model.PanelTab == PanelTab.All`. `SegmentedPivots` shows four segments.
- [ ] **Step 5: `dotnet test Elevate.sln`; all pass.**
- [ ] **Step 6: Commit** `Windows: add an All tab that lists every kind of role`.

---

### Task 2: Panel selection

**Files:**
- Create: `VM/PanelSelection.cs`, `VM/AppModel.Selection.cs`
- Modify: `src/Elevate.App.Model/Services/AppSettings.cs` (new `panelTenant` key in `FileModel`), `VM/AppModel.cs` (two properties), `VM/AppModel.Panel.cs:133,144` (`VisibleTenants`, `VisibleIdentities`)
- Test: `tests/Elevate.App.Tests/AppModelSelectionStateTests.cs`

**Interfaces:**
- Consumes: Task 1's `ListRows`.
- Produces:

```csharp
public abstract record PanelSelection {
    public sealed record AllTenants : PanelSelection;
    public sealed record Tenant(TenantKey Key) : PanelSelection;
    public sealed record Profile(Guid Id) : PanelSelection;
}
public abstract record PanelContent {
    public sealed record Home : PanelContent;
    public sealed record RoleList : PanelContent;
    public sealed record Tenant(TenantKey Key) : PanelContent;
    public sealed record Profile(Guid Id) : PanelContent;
}

// AppSettings
public TenantKey? PanelTenant { get; set; }      // settings.json key "panelTenant"; null = all tenants

// AppModel
public Guid? SelectedProfile { get; private set; }   // never persisted
public bool Browsing { get; set; }                   // never persisted
public TenantKey? ScopeTenant { get; set; }          // Settings.PanelTenant, null unless tracked and its account does not need sign-in
public PanelSelection PanelSelection { get; }
public void Select(PanelSelection selection)
public PanelContent PanelContent { get; }
public int OtherTenantMatchCount { get; }
public void SearchAllTenants()
public bool HandleEscape()                           // false when there was nothing to step back from
```

Every setter and method that changes state ends in `Touch()`.

Rules the tests pin:
- `PanelSelection` is `Profile` when `SelectedProfile` names an existing profile, else `Tenant` when `ScopeTenant` is non-null, else `AllTenants`.
- `Select(AllTenants)` and `Select(Tenant)` clear `SelectedProfile` and leave `SearchQuery`, `SelectMode` and `Selection` alone. `Select(Profile)` leaves `ScopeTenant` alone.
- `PanelContent`: `Profile` for a profile selection; `Tenant` for a tenant selection; otherwise `RoleList` when `Browsing || IsFiltering || SelectMode || HomeIsEmpty`, else `Home`. Declare `HomeIsEmpty` here returning `false`; Task 3 replaces it.
- With `ScopeTenant` set, `VisibleTenants(identityId)` returns only that tenant and `VisibleIdentities` only its account.
- `OtherTenantMatchCount` is 0 unless `ScopeTenant != null && IsFiltering`; then the number of roles in all other tracked tenants matching the query and the current tab's kinds.
- `HandleEscape()`: a non-empty query is cleared (true); else a profile selection returns to what is underneath (true); else if `Browsing`, `SelectMode` or `ScopeTenant` is set, all three are cleared (true); else false.

- [ ] **Step 1: Write the failing tests** in `AppModelSelectionStateTests`: two identities with one tenant and one Entra role each (`"Global Reader"`, `"Billing Admin"`). One `[Fact]` per line:

```csharp
DefaultsToAllTenantsAndHome
SelectingATenantPersistsAndScopesTheList            // Settings.PanelTenant == key; VisibleTenants("id-2") empty; PanelContent is Tenant(key)
ARemovedTenantFallsBackToAllTenants
AnAccountNeedingSignInFallsBackToAllTenants
TypingBrowsingOrSelectModeShowTheRoleList
SelectingATenantKeepsQueryAndSelection
OtherTenantMatchesAreCountedOnlyWhenScopedAndFiltering   // scoped to tenant 1, "billing": own rows empty, count 1; "zzz": 0; unscoped: 0
SearchAllTenantsKeepsTheQuery
EscapeClearsTheFilterThenReturnsHomeThenReportsNothingLeft   // true, true, false
EscapeOnAProfileReturnsToTheTenantUnderneath
AMissingProfileIsNotASelection
PanelTenantRoundTripsThroughSettingsJson
```

- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.** In `Shell/FlyoutWindow.xaml.cs` `OnKeyDown` (:176), Escape calls `Model.HandleEscape()` and hides the flyout only when it returns false.
- [ ] **Step 4: `dotnet test Elevate.sln`; all pass.**
- [ ] **Step 5: Commit** `Windows: panel selection state for all tenants, one tenant and a profile`.

---

### Task 3: Home and sidebar data

**Files:**
- Create: `VM/AppModel.Home.cs`
- Modify: `VM/AppModel.Panel.cs:188` (`ActiveAssignmentsOrdered`), `VM/AppModel.Approvals.cs:50`, `src/Elevate.App/Views/PanelItems.cs:1134`
- Test: `tests/Elevate.App.Tests/AppModelHomeTests.cs`; update the `AppModelPanelTests` case that expects Active now to follow the tab

**Interfaces:**
- Produces:

```csharp
public IReadOnlyList<ActiveAssignment> ActiveAssignments(TenantKey? tenant)   // every kind, ActiveSummary.Order, search-filtered as today; null = all tenants
public IReadOnlyList<ApprovalRequest> ApprovalsIn(TenantKey? tenant)          // ApprovalsOrdered filtered by tenant
public bool CanExtendNow(RoleKey key, DateTimeOffset now)                     // the condition PanelItems.cs:1134 uses to offer Extend
public ActiveAssignment? HeroAssignment(DateTimeOffset now)
public bool HomeIsEmpty { get; }
public readonly record struct TenantMark(TenantMarkKind Kind, int Count);
public enum TenantMarkKind { None, Active, Error }
public TenantMark SidebarMark(TenantKey key)
public int TotalActiveCount { get; }
```

Rules: `ActiveAssignmentsOrdered` becomes `ActiveAssignments(null)` and no longer depends on `PanelTab`. `HeroAssignment` is the first of `ActiveAssignments(null)` when `CanExtendNow` holds for it, else null. `HomeIsEmpty` is true when `ApprovalsIn(null)` and `ActiveAssignments(null)` are empty. `SidebarMark` is `Active(n)` for `n > 0` entries of `Active` in that tenant (any status or kind), else `Error` when the tenant has a discovery error (the condition `PanelListBuilder.ApplyStatus` shows as an error issue), else `None`.

- [ ] **Step 1: Write the failing tests** in `AppModelHomeTests`:

```csharp
ActiveNowListsEveryKindWhateverTheTab
ActiveAssignmentsCanBeNarrowedToOneTenant
ApprovalsCanBeNarrowedToOneTenant
HeroIsTheSoonestExpiryInsideTheExtendWindow        // ends in 10 min
NoHeroOutsideTheExtendWindow                       // ends in 60 min
NoHeroWhenTheSoonestRowIsPending
HomeIsEmptyWithNothingActiveAndNoApprovals         // and PanelContent is RoleList
SidebarMarkPrefersActiveCountOverError
```

Use the policy `ExtendWindowTests` in `tests/Elevate.Core.Tests/PanelHelperTests.cs:47` uses for an extendable role.

- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement**, and make `PanelListBuilder.Fill` call `CanExtendNow`.
- [ ] **Step 4: `dotnet test Elevate.sln`; all pass.**
- [ ] **Step 5: Commit** `Windows: home, hero and sidebar data in the app model`.

---

### Task 4: Profile run session

**Files:**
- Create: `VM/ProfileRunSession.cs`, `VM/AppModel.ProfileRun.cs`
- Modify: `VM/AppModel.Refresh.cs:14` (`PanelOpened`), `VM/AppModel.Profiles.cs` (delete path), `VM/AppModel.Activation.cs` (the outcome notification `QuickRunAsync` posts becomes callable from `SubmitRunAsync`), `src/Elevate.App/Shell/FlyoutWindow.xaml.cs:103` (`Hide`)
- Test: `tests/Elevate.App.Tests/AppModelProfileRunTests.cs`

**Interfaces:**
- Consumes: Task 2's `SelectedProfile`, `Select`.
- Produces:

```csharp
public sealed class ProfileRunSession : ObservableObject {
    public Guid ProfileId { get; }
    public IReadOnlyList<ProfilePlanItem> Items { get; }          // durations editable through SetDuration
    public void SetDuration(RoleKey key, TimeSpan duration);
    public ISet<RoleKey> Excluded { get; }
    public string Justification { get; set; }
    public string TicketNumber { get; set; }
    public string TicketSystem { get; set; }
    public bool ScheduleStart { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public bool Running { get; }
    public bool Finished { get; }
    public bool ResultsSeen { get; }
    public IReadOnlyDictionary<RoleKey, ActivationResult> RowResults { get; }
    public IReadOnlyList<ProfilePlanItem> ToActivate { get; }
    public bool NeedsTicket { get; }
    public bool JustificationRequired { get; }
    public bool CanSubmit(DateTimeOffset now);
}

// AppModel
public IReadOnlyDictionary<Guid, ProfileRunSession> RunSessions { get; }
public bool PanelIsOpen { get; }
public ProfileRunSession RunSession(Guid id)        // existing, else a fresh plan
public void ResetRunSession(Guid id)                // what RunProfileWindow.Load() does today
public Task SubmitRunAsync(Guid id)                 // what RunProfileWindow.OnSubmit does today
public void ShowProfile(Guid id)                    // Select(new PanelSelection.Profile(id)); a finished session is reset first
public void MarkRunSeen(Guid id)
public void PanelClosed()
```

`ToActivate`, `NeedsTicket`, `JustificationRequired`, `CanSubmit`, the plan-and-prefill logic and the submit logic are the code now in `RunProfileWindow.xaml.cs` (:65-80, `Load` :88, `OnSubmit` :343), moved, with control reads replaced by session properties.

Rules:
- Leaving a profile (`SelectedProfile` changes away from `id`) removes its session unless it is `Running`.
- `SubmitRunAsync` posts the outcome notification (the one `QuickRunAsync` posts, titled with the profile name) when it ends and `PanelIsOpen` is false and no run window for that profile is open. To know the latter, add `public ISet<Guid> RunWindowsOpen { get; }` that `RunProfileWindow` adds to on open and removes from on close.
- `PanelOpened()` sets `PanelIsOpen = true`, `Browsing = false`, then: if `SelectedProfile`'s session is finished and seen, remove it and clear `SelectedProfile`; else if `SelectedProfile`'s session exists, keep it; else select the first session that is running, or finished and unseen; else clear `SelectedProfile`.
- Deleting a profile, or a managed profile disappearing, removes its session and clears `SelectedProfile` when it names it.

- [ ] **Step 1: Write the failing tests** in `AppModelProfileRunTests`, with a saved profile `"Ops"` over one Entra role and the activation stub `AppModelProfileTests` uses:

```csharp
ASessionPlansTheProfileAndPrefillsTheReason
UncheckingAnEntryRemovesItFromToActivate
LeavingAnUnstartedDraftDropsIt
ADraftSurvivesThePanelClosing
ARunningSessionSurvivesLeavingAndReopens
UnseenResultsReopenOnTheProfile
SeenResultsDoNotReopen
ARunThatEndsWithThePanelClosedNotifies              // TestModel's notifier recorded one toast titled "Ops"
ARunThatEndsWithThePanelOpenDoesNotNotify
DeletingTheShownProfileClearsSelectionAndSession
ShowingAFinishedProfileAgainStartsAFreshPlan
```

- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.** `FlyoutWindow.Hide()` calls `Model.PanelClosed()`.
- [ ] **Step 4: `dotnet test Elevate.sln`; all pass.**
- [ ] **Step 5: Commit** `Windows: keep a profile run in the app model so it survives the flyout hiding`.

---

### Task 5: Shared run-profile content

**Files:**
- Create: `src/Elevate.App/Views/RunProfileContent.xaml`, `src/Elevate.App/Views/RunProfileContent.xaml.cs`
- Modify: `src/Elevate.App/Views/RunProfileWindow.xaml`, `src/Elevate.App/Views/RunProfileWindow.xaml.cs`

**Interfaces:**
- Consumes: Task 4's session API.
- Produces: `public sealed partial class RunProfileContent : UserControl` with `void Bind(AppModel model, Guid profileId, bool compact)`, `event EventHandler? Done` and `void FocusReason()`. `compact: false` is today's 600-wide window layout with Cancel and Done; `compact: true` fits 380 wide (duration picker and status under the role name) and shows only `Activate N`.

- [ ] **Step 1: Move the rows, inputs and buttons** from `RunProfileWindow` into `RunProfileContent`, reading and writing the session instead of window fields; it redraws on `AppModel.Changed`. `RunProfileWindow` keeps `DialogWindows.Configure(this, "Run profile", 600, 560, Root, autoHeight: true)`, hosts the control, closes on `Done`, maintains `RunWindowsOpen`, and on a `RunRequests` change calls `model.ResetRunSession(id)` when the session is not running.
- [ ] **Step 2: Build** `dotnet build src/Elevate.App/Elevate.App.csproj` and run `dotnet test Elevate.sln`; all pass.
- [ ] **Step 3: Check the window by hand:** right-click a profile chip, Run…: same look and behaviour as before, including Enter to submit and a second Run… re-planning.
- [ ] **Step 4: Commit** `Windows: split the run window into a control the flyout can host`.

---

### Task 6: Sidebar and flyout frame

**Files:**
- Create: `src/Elevate.App/Views/PanelSidebar.xaml`, `src/Elevate.App/Views/PanelSidebar.xaml.cs`
- Modify: `src/Elevate.App/Shell/FlyoutWindow.xaml:7`, `src/Elevate.App/Shell/FlyoutWindow.xaml.cs:23-24`, `src/Elevate.App/Views/PanelView.xaml` (width, remove `ProfilesRow` :296 and the footer's Add account), `src/Elevate.App/Views/PanelView.xaml.cs` (`Draw`, remove `DrawProfiles` :272 and `Chip` :320), `src/Elevate.App/Views/ProfileActions.cs:19`, `src/Elevate.App/Views/AllProfilesFlyout.cs:214`

**Interfaces:**
- Consumes: `PinnedProfiles`, `Profiles`, `PanelSelection`, `Select`, `ShowProfile`, `SidebarMark`, `TotalActiveCount`, `CollapsedIdentities`, `ToggleIdentity`, the sign-in-needed state `PanelListBuilder.MarkSignInNeeded` reads.
- Produces: `PanelSidebar : UserControl` with `void Draw(AppModel model)`; `ProfileActions.RunAsync(AppModel model, Guid id, bool silentlyIfPossible, bool inPanel)` where `inPanel: true` calls `model.ShowProfile(id)` instead of `App.Current.OpenRunProfile(id)`, also as the fallback of a silent run; constants `FlyoutWindow.Width = 516`, `MaxHeight = 720`, and `PanelView` laid out as a 136-wide sidebar column, a 1-px divider and a 380-wide main column.

Layout, top to bottom (spec 3.2):
- **Profiles**, fixed: heading with the `All N` button (opens `AllProfilesFlyout.Show`); one button per pinned profile (star glyph for a one-entry profile, bolt otherwise), tooltip with name, `ProfileSummary.Caption` and the shortcut when it is the hot-key profile; click → `ProfileActions.RunAsync(model, id, PanelView.IsControlDown(), inPanel: true)`; right-click → `ProfileActions.Menu(...)`. Hidden with no profiles; only `All N` with none pinned. A pinned profile shown in the main view is highlighted; an unpinned one highlights `All N`.
- **Tenants**, in a `ScrollViewer`: `All tenants` with `TotalActiveCount`; per identity a label (address, click toggles collapse) with the account menu `OnGroupMenu` builds for identity groups, or a `Sign in` button when sign-in is needed (tenant rows dimmed and disabled); tenant rows with name and mark (count in the success color, or the warning glyph), tooltip with the tenant's status issues.
- `Add account…` fixed at the bottom.
- With no accounts or when not configured, the sidebar shows only `Add account…`.
- Each row is a `Button` with `AutomationProperties.Name` including its state ("Contoso, 2 active").

- [ ] **Step 1: Build `PanelSidebar`** and call `Sidebar.Draw(model)` from `PanelView.Draw`, redrawing only when a signature string changes (as `_profilesSignature` does today).
- [ ] **Step 2: Reframe** the flyout and `PanelView`; move the tenant-menu and account-menu builders out of `OnGroupMenu` into methods both the list header and the sidebar call. `AllProfilesFlyout.Run` and the `Run…` item of `ProfileActions.Menu` pass `inPanel: true` when opened from the flyout; `ManageProfilesWindow` and the hot-key path pass `false`.
- [ ] **Step 3: Build and `dotnet test Elevate.sln`; all pass.**
- [ ] **Step 4: Check by hand** (launch per `windows/README.md`): selection changes the list's scope; Ctrl-click a profile runs silently; right-click shows the menu; a signed-out account shows `Sign in`. Review Focus 5: enough tenants to scroll the sidebar, a 60-character tenant name, and display scaling at 100%, 150% and 200%: the flyout is 516 logical pixels wide, at most 720 tall, anchored to the tray, nothing clipped.
- [ ] **Step 5: Commit** `Windows: sidebar with pinned profiles, accounts and tenants`.

---

### Task 7: Role list, tenant view and Home

**Files:**
- Modify: `src/Elevate.App/Views/PanelItems.cs` (`PanelListBuilder.Build` :702 and new item types, `PanelItemTemplateSelector` :675), `src/Elevate.App/Views/PanelView.xaml` (templates, header), `src/Elevate.App/Views/PanelView.xaml.cs` (`Draw`, header, click handlers)

**Interfaces:**
- Consumes: `PanelContent`, `ApprovalsIn`, `ActiveAssignments`, `HeroAssignment`, `HomeIsEmpty`, `OtherTenantMatchCount`, `SearchAllTenants`, `Browsing`, `ScopeTenant`.
- Produces, in `PanelItems.cs`:

```csharp
public enum PanelAction { BrowseAll, SearchAllTenants, SelectRoles }
public sealed class ActionRow : PanelItem { public PanelAction Action { get; } public string Text { get; } public string? Caption { get; } }
public sealed class EmptyCardRow : PanelItem { public string Title { get; } public string Body { get; } }
public sealed class HeroRow : PanelItem   // wraps the RoleRow of the hero assignment: Name, Caption, TimeLeft, and the same Extend / Deactivate handlers
// GroupKind gains: Eligible
```

`PanelListBuilder.Build(model, now)` by `model.PanelContent`:
- **Home:** Approvals group (`ApprovalsIn(null)`); Active now group with `HeroRow` first when `HeroAssignment(now)` is non-null, then plain summary rows; one `ActionRow(BrowseAll, "Browse all roles", "N in M tenants")`.
- **RoleList:** when `HomeIsEmpty` and not filtering, `EmptyCardRow("Nothing active", "You hold no elevated access right now.")` first; then today's account and tenant groups (no Approvals or Active now groups); when `HomeIsEmpty`, no profiles exist and not filtering, a last `ActionRow(SelectRoles, "Select roles", …)`.
- **Tenant(key):** Approvals group (`ApprovalsIn(key)`); Active now group (plain rows, no hero); an `Eligible` group titled `Eligible · N` holding `AddTenantRows` for that tenant with no account or tenant header; `No matches` note when it has none for the query; `ActionRow(SearchAllTenants, "N more in other tenants")` when `OtherTenantMatchCount > 0`.
- **Profile:** an empty list (Task 8 shows the profile control instead).

- [ ] **Step 1: Implement the item types, templates and `Build` branches.** The segmented tab control moves from the header area to directly above the eligible rows on RoleList and Tenant, and is hidden on Home.
- [ ] **Step 2: Header.** For Home and RoleList: `Elevate`, `HeaderCaption`, the offline pill. For Tenant: tenant name, `home` tag, account address, an `Access packages…` button when the tenant offers them, and the tenant menu button. Select and refresh buttons on all of these. The search box placeholder is `Filter roles in <tenant>` on a tenant view.
- [ ] **Step 3: Click handlers.** `BrowseAll` sets `Browsing = true`; `SearchAllTenants` calls the model; `SelectRoles` sets `SelectMode = true`.
- [ ] **Step 4: Build and `dotnet test Elevate.sln`; all pass.**
- [ ] **Step 5: Check by hand:** Home with an approval, a role expiring in under 15 minutes (hero) and one with longer (plain); nothing active (card plus list); each tab on both scopes; Azure tree and select mode on a tenant view; a tenant-view filter with matches elsewhere (Review Focus 4); Escape steps back, then closes.
- [ ] **Step 6: Commit** `Windows: home, role list and one-tenant views in the flyout`.

---

### Task 8: Profile view

**Files:**
- Modify: `src/Elevate.App/Views/PanelView.xaml`, `src/Elevate.App/Views/PanelView.xaml.cs`

**Interfaces:**
- Consumes: `RunProfileContent`, `RunSession`, `MarkRunSeen`, `SetPinned`, `ProfileActions.Menu`.

- [ ] **Step 1: Host the control.** When `PanelContent` is `Profile(id)`: hide the search box, the select button and the list; show a header with the bolt or star, profile name, `N roles · M tenants`, the shortcut when bound, a pin toggle (`SetPinned`, with `ProfileActions.PinRefusedHint` on refusal), a menu button (`ProfileActions.Menu`) and refresh; below it `RunProfileContent` bound with `compact: true`, `FocusReason()` on show. Call `MarkRunSeen(id)` whenever the control is visible and the session is finished.
- [ ] **Step 2: Build and `dotnet test Elevate.sln`; all pass.**
- [ ] **Step 3: Check by hand:** click a sidebar profile, Enter submits when valid; run one that needs MFA: the flyout hides, the toast arrives, reopening shows the results (Review Focus 3); reopen again: Home; Run from the All list on an unpinned profile highlights `All N`; the global shortcut still opens the window when input is needed.
- [ ] **Step 4: Commit** `Windows: show and run a profile in the flyout's main view`.

---

### Task 9: Guides

**Files:**
- Modify: `windows/README.md` (flyout section), the Windows paragraphs in `docs/getting-started.md` and `docs/profiles-and-shortcuts.md`
- Replace: `docs/images/panel-windows.png`, `docs/images/tutorials/windows-profiles-row.png`, `docs/images/tutorials/windows-manage-profiles.png` if the flyout appears in it

- [ ] **Step 1: Update the Windows text** to the new layout; say Ctrl-click where it applies.
- [ ] **Step 2: Retake the Windows screenshots** from the running app with the sample data the current ones use; keep file names (rename `windows-profiles-row.png` only together with every link to it).
- [ ] **Step 3: Commit** `Docs: describe the Now panel on Windows`.
