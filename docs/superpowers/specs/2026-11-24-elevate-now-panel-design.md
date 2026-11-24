# Elevate — the "Now" panel

Date: 2026-11-24. Replaces the panel layout from `2026-09-04-pimtray-design.md`,
`2026-09-05-elevate-daily-panel-design.md` and the profiles row from
`2026-09-05-elevate-profiles-design.md`. Sheets and windows opened from the panel are unchanged
unless section 4 says otherwise.

Mockups (dark, static): https://claude.ai/artifact/QLsnkjNRKK6j4bQSXukrbQ, page "B · Now,
refined". Artboards: Home · all tenants, One tenant, Roles: search and browse, Roles: select mode,
Nothing active, Profile selected, Home · Windows, and the shared sidebar. The mockups fix layout,
content and wording; they are not pixel specifications.

Lines marked **(call)** were decided by the author of this document, not by the product owner, and
are the first things to challenge in review.

## 1. Intent

The panel is opened mostly to see what is active and act on it, by someone who often has several
accounts and many tenants. Today it is one long list split by API (Entra, Azure, Groups) and nested
account → tenant → role, so with many tenants the things that matter are far apart, tenants are
only visible by scrolling, and profiles (which span tenants) sit in a row that scrolls with the
rest.

The new panel has two parts:

- a **sidebar** that is always visible: pinned profiles on top, then every account with its
  tenants;
- a **main view** that shows one of: the cross-tenant home, one tenant, the role list, or one
  profile.

## 2. Goals, non-goals, anti-goals

Goals:

- Every account and tenant is visible and one click away, with its state (active count, error,
  sign-in needed) readable without opening it.
- Pinned profiles stay in the same place whatever the main view shows.
- What is active, what is about to expire and what waits for a decision is the first thing shown.
- Nothing the panel can do today is lost. Section 6 lists where each existing element goes.
- macOS and Windows ship the same design in the same release.

Non-goals:

- No usage tracking and no "most used" list. Favorites (one-role profiles) already cover it.
- No change to the activation, approval, save-profile, access-package, Settings, add-account or
  setup sheets and windows, or to notifications and the menu bar / tray icon.
- No change to the CLI: it has no panel.
- No separate windowed app.

Anti-goals:

- A panel that needs configuring before it is useful. A new user with one account and no profiles
  must see their eligible roles immediately (section 3.6).
- Any path where a single click activates something that today asks first.

## 3. The panel

### 3.1 Frame

- Width 516 pt: sidebar 136, main view 380 (today's panel width). Height grows with content to a
  maximum of 720 **(call)**; the sidebar's tenant list and the main view's content scroll
  independently.
- The main view has, top to bottom: header, filter field, banners, content, footer. A profile view
  (3.5) has no filter field and no select-roles toggle **(call)**.
- **Header**: a title on the left; on the right the select-roles toggle and the refresh button,
  with today's shortcuts and disabled-when-offline rule. The title is "Elevate" with the
  organization caption beneath it and the `offline` pill on Home and the role list; the tenant
  name on a tenant view; the profile name on a profile view **(call: the "Elevate" mark and
  organization caption are not shown on tenant and profile views)**.
- **Filter field**: "Filter roles", focused when the panel opens, as today.
- **Banners**: the notice, update-available and token-hint banners, unchanged, under the filter.
- **Footer**: Settings… and Quit. Add account… moves to the bottom of the sidebar.

### 3.2 Sidebar

Top section, fixed (does not scroll): **Profiles**.

- One button per pinned profile, in the order they are pinned, at most `ProfilePins.limit` (four).
  A favorite (one-role profile) shows a star, others the bolt. Names truncate; the tooltip gives
  the full name, the role count and, for the profile bound to the global shortcut, the shortcut.
- "All N" opens today's All list unchanged. With profiles but none pinned the section shows only
  "All N"; with no profiles the section is hidden.
- Click selects the profile (3.5). Option-click (Ctrl-click on Windows) runs it silently, as a chip
  does today. Right-click gives today's chip menu.

Scrolling section: **Tenants**.

- "All tenants" with the total active count. Selecting it shows Home.
- Then each account: its address as a group label with the account menu (today's menu, unchanged)
  and, under it, one row per tracked tenant in today's order.
- A tenant row shows the tenant name and one trailing mark: the active count in green when it has
  active or pending assignments, else the red warning glyph when discovery failed. Its tooltip
  carries what the tenant header pills say today ("home", "manual roles", "Azure roles only",
  the error text).
- An account that needs sign-in shows a "Sign in" button under its address and its tenants are
  dimmed and not selectable. Its menu stays, so the account can still be signed out or removed
  **(call, changed during implementation: the first draft replaced the menu with the button)**.
- Clicking an account label collapses or expands its tenants (`collapsedIdentities`, as today).
- "Add account…" sits at the bottom, outside the scrolling area.

The selected sidebar item (All tenants, a tenant, or a pinned profile) is highlighted. When the
main view shows a profile that is not pinned, "All N" is highlighted instead **(call)**.

### 3.3 Home (All tenants selected, no filter, not browsing)

In order; a section is absent when it has nothing to show:

1. **Approvals · N** — today's approval rows (requester, role or group, then tenant · duration ·
   age; Approve and Deny, or "Decide in the portal").
2. **Active now · N** — every active, pending and scheduled assignment across all accounts in
   `ActiveSummary.order`, with today's controls and captions (tenant, and "propagating (~N min)",
   "awaiting approval", "starts in …", "provisioning", "not in effect yet"). When the first row is
   active and inside the extend window (`ExtendWindow.canExtend`), it is drawn as a card: larger
   time left, amber outline, **Extend** as the primary button and Deactivate beside it
   **(call: only that one row, only inside the extend window; otherwise all rows are plain)**.
3. **Browse all roles** — one row with the count of eligible roles and tenants. It switches the
   main view to the role list (3.4).

### 3.4 Role list

Shown when the user chooses Browse all roles, types in the filter, turns on select mode, or selects
a tenant.

- A segmented control **All · Entra · Azure · Groups**. "All" is new: it shows the three kinds
  together, per tenant in the order Entra roles, the Azure scope tree, then groups. Group rows
  carry the caption "Group membership" or "Group ownership" so they can be told from roles. The
  choice is persisted as today. When no tab has been stored the panel starts on All; a stored tab
  is kept **(call: a user who never left the Entra tab has nothing stored and moves to All)**.
  The control narrows the eligible rows only. Approvals and Active now always show every kind, on
  Home, on a tenant view and under the "Nothing active" card. A tenant header's active count in
  the all-tenants list follows the control (every kind on All); sidebar counts are every kind.
- With All tenants selected the list is grouped as today: account header, tenant header (name,
  pills, active count, access-package icon, tenant menu), rows. Tenant headers still collapse.
- Role rows are today's rows unchanged: state dot, name, "new" badge, captions (MFA, approval,
  via group, manual, scope), the trailing control for the state, Option-click quick activation,
  right-click Add to favorites, scope headers with counts on Azure.
- **One tenant selected**: the header title is the tenant name with its "home" tag and the account
  address beneath; an **Access packages…** button appears when the tenant offers them; the tenant
  menu is the ⋯ button. The filter placeholder reads "Filter roles in <tenant>". Content is that
  tenant's Approvals, its Active now rows (plain rows, no card), then the All · Entra · Azure ·
  Groups control's "Eligible · N" with its rows. No account or tenant headers.
- **Filtering** respects the selection: all tenants, or the selected one. On a tenant view, when
  the query also matches rows in other tenants, a last row "N more in other tenants" selects All
  tenants and keeps the query **(call)**. Down, Up and Return behave as today. Escape clears the
  filter; with an empty filter it returns to Home **(call)**.
- **Select mode** is today's: checkboxes on rows and on Azure scope headers, a footer with the
  selection breakdown, **Save as profile…** and **Activate N roles**. Selecting a different
  tenant or All tenants keeps the selection, as switching tabs does today.

### 3.5 Profile view

Selecting a profile in the sidebar shows, in the main view, the content of today's run sheet:

- Header: profile name, "N roles · M tenants", the global shortcut if bound, the pin star, and a
  menu with Edit…, Manage profiles…, Deactivate… and Delete… (today's actions; managed profiles
  keep today's restrictions).
- Entries grouped by tenant and account, each with its checkbox and duration; entries already
  active, pending or no longer eligible are marked skipped, as today.
- Reason (remembered, pre-filled), ticket fields when a policy asks for them, Start at.
- A line saying how many will activate and whether MFA will open the browser, and the
  **Activate N** button. Nothing is requested before it is pressed.
- While running, each row shows its progress; when finished the results stay until the user
  selects something else **(call)**.
- Keyboard: the Reason field has the focus when the view appears. Return submits when the form is
  valid, as in today's sheet. Escape returns to the previous all-tenants or tenant selection.

The panel closes whenever it loses focus, and an MFA step opens the browser, so a run cannot live
in view state **(call, all of this paragraph)**. The app model holds one run session per profile
view: the unticked entries, durations, reason, ticket fields, Start at, per-row progress and
results. Closing the panel does not cancel a run or discard a draft. Reopening the panel shows that profile view when it was
showing an unstarted draft at close, a run is in progress, or a run finished with results not yet
seen; otherwise it opens as section 5 says. The session is dropped when the user selects something else after the
run finished, when they leave an unstarted draft, and when the app quits. The outcome
notification is posted as today when the run ends with the panel closed.

Which surface a profile opens in:

- **Panel profile view**: a click on a sidebar profile, a row or Run in the All list, Run… in a
  profile's menu, and an Option-click silent run from the panel that turns out to need input.
  This includes profiles that are not pinned.
- **Run-sheet window** (kept): the global shortcut and a notification action when they need
  input, and Run… in the Profiles window. The panel is not open in these cases.

Both render the same content view over the same run session.

### 3.6 Nothing active, or nothing set up

- All tenants selected, no approvals and nothing active or pending: Home shows a "Nothing active"
  card and, directly under it, the role list of 3.4 (so no Browse step). When the user has no
  profiles a one-line hint at the bottom offers **Select roles** to save one.
- Not configured, startup error, no accounts: today's setup, error and no-accounts views fill the
  main view; the sidebar shows only Add account….

### 3.7 Changes after the first hands-on test (2026-11-24)

These override 3.4 and 3.5 where they differ. Both platforms.

- **Access packages button.** In the tenant header it is drawn as a button (bordered, small), not
  as plain text beside the tenant name.
- **Access packages in the panel.** The button no longer opens a window. The main view shows the
  tenant's access packages (the content of today's access packages window, laid out for 380
  wide), with the tenant header kept and a back control to the tenant's role list. Requesting a
  package shows the request form in the main view too; submitting or cancelling returns to the
  package list. Escape steps back one level (form, then package list, then role list). Selecting
  anything in the sidebar leaves it. The access packages window and the request sheet/window are
  removed if nothing else opens them.
- **Tabs in the Eligible bar.** The All / Entra / Azure / Groups control moves into the
  `Eligible · N` header row, right-aligned and small. It filters only the eligible list;
  `Active now` stays a separate block above. On Home's embedded list (3.6) and the all-tenants
  list the control sits in the first header row above the eligible rows.
- **Compact durations in the profile view.** Each role keeps its own duration, shown as a small
  borderless menu (`5 h ⌄`) at the trailing edge of the role's first line, in place of the
  full-width picker under the name. `MFA` and status move to the left of it or to the detail line.
  The run window (non-compact) may keep its wider layout.

- **Sidebar sections collapse, and rows share one look.** (Chosen over a Profiles/Tenants toggle,
  which would hide tenant status or put profiles a click away.) `PROFILES` and `TENANTS` are
  clickable headers with a disclosure chevron; each collapses its section, and the two states are
  remembered in settings (both expanded by default). A 1-px divider separates the sections.
  Collapsed, `PROFILES` shows the profile count and `TENANTS` shows the total active count plus
  the warning glyph when any account needs sign-in or any tenant has an error. Profile rows lose
  their filled pill: they are flat rows (glyph + name) like tenant rows. Exactly one row is
  filled, in the accent colour with the text in the on-accent colour: the selected profile, tenant
  or `All tenants`. Hover uses the subtle row hover on both kinds. The `Sign in` row keeps its
  warning tint. `All N` stays in the Profiles header when expanded. The headers are buttons with
  an accessible name and expanded/collapsed state.

## 4. What changes outside the panel

- `RunProfileView` (macOS) and `RunProfileWindow` (Windows) are split into a content view used by
  both the window and the panel's profile view.
- The profiles row under the tabs (`ProfilesRow`, its Windows counterpart) is removed; the All list
  it opened is kept and opened from the sidebar.
- User guides and their screenshots: getting-started (section 5), activating-roles,
  profiles-and-shortcuts, approvals, access-packages (the tenant entry point). The canvas is saved
  under `docs/design/` like the earlier ones.

## 5. State

- A panel selection: all tenants, one tenant, or one profile. All tenants and a tenant are
  persisted like the tab is today; a profile selection is not, and the panel reopens on the last
  all-tenants or tenant selection **(call)**. A persisted tenant that no longer exists falls back
  to all tenants.
- A profile run session (3.5) lives in the app model for the life of the app process and is never
  written to disk.
- "Browsing" (Home replaced by the role list) is not persisted; the panel opens on Home.
- `PanelTab` gains `all`.
- Existing persisted state keeps its meaning: collapsed accounts, tenants, scopes, Active now and
  Approvals; the filter never survives closing the panel.

## 6. Where today's elements go

| Today | New place |
|---|---|
| Header title, organization caption, offline pill | Main view header (Home and role list) |
| Select roles, Refresh | Main view header, every view |
| Entra / Azure / Groups tabs | Role list control, with All added |
| Profiles row, All N | Sidebar, Profiles |
| Filter field, notice / update / token-hint banners | Main view, unchanged |
| Approvals section | Home; tenant view (that tenant's) |
| Active now section | Home; tenant view (that tenant's) |
| Account row and menu, sign-in needed | Sidebar account label; account header in the all-tenants role list |
| Tenant header, pills, menu, access-package icon | Sidebar row and tooltip; tenant view header; tenant header in the all-tenants role list |
| Role rows, Azure scope tree, select mode | Role list, unchanged |
| Run profile sheet | Profile view; window kept for shortcut and notification |
| Add account… | Sidebar bottom |
| Settings…, Quit | Main view footer |
| Setup, startup error, no accounts | Main view |

## 7. Platforms

- **macOS**: SwiftUI, `PanelView` and the views under `Views/`. Native materials and controls;
  light and dark follow the system.
- **Windows**: `FlyoutWindow` and `PanelView.xaml` with Fluent controls; the flyout width becomes
  516 and its height cap 720. Ctrl-click where macOS uses Option-click. The Windows mockup covers
  Home only; the other views follow the macOS mockups in Fluent styling.
- **CLI**: no change.

## 8. Approaches considered

- **A · Command bar** — search-first, one flat list with type tags, keyboard hints. Fast for
  someone who knows the role name; weak for seeing state or browsing, and tenants are invisible.
- **B · Now** — opens on what is active, approvals and profiles; the list is one step away.
  Matches why the panel is opened. Chosen.
- **C · Window** — sidebar for tenants and sections, a table of roles, activation composed inline.
  Best for many tenants but no longer a glance.

The design is B for the main view with C's sidebar, added when multi-account, multi-tenant use
turned out to be the normal case, and A's focused filter and Return-to-activate, which the app
already has.

## 9. Left to the builder

- View decomposition, naming, and how the shared run-profile content is factored.
- Spacing, type sizes and colors beyond what the mockups show; use system materials and semantic
  colors, not the mockups' hex values.
- Transitions between main views.
- Accessibility labels for the new sidebar items, following the existing ones.

## 10. Testing

- App-model tests (macOS `ElevateAppTests`, Windows view-model tests): selection and its
  persistence and fallback; the All tab's contents and order; Home's sections and the card rule;
  filter scope and the "more in other tenants" count; select mode surviving a selection change;
  the empty-state rule of 3.6; sidebar marks for active, error and sign-in needed.
- The run-profile content view behaves the same in the window and in the panel.
- A run session survives the panel closing: a draft, a run in progress and unseen results each
  reopen on the profile view; a seen result and a quit do not.
- Each profile entry point opens the surface 3.5 assigns it, pinned or not.
- Existing panel, selection, profile and scope-tree tests keep passing or are moved with the code
  they cover.
