# Profiles and shortcuts

A profile is a saved set of roles that you activate together with one click: the roles for
your morning routine, for an incident, for a release day. This guide shows how to create, run,
edit and trigger them.

> The pictures in this guide are real renders of the app with sample data from a fictional
> organization.

## Save a profile

1. Click the **select roles** button in the panel header.
2. Tick the roles you want, on any tab and in any tenant. The footer counts what you have
   selected.
3. Click **Save as profile…**.

![Select mode with three roles ticked and the Save as profile button](images/tutorials/panel-select.png)

Name the profile. The sheet lists what running it will ask for today: the duration Elevate
remembers for each role, or the policy default, and which roles need approval.

![The Save as profile sheet with a name field and the three roles with their durations](images/tutorials/save-profile.png)

Click **Save**. The reason you type on a run is remembered per role, so later runs are pre-filled
with it.

## Pin and run a profile

Under the tabs sits one row: a chip for each **pinned** profile, and **All N** for the rest. Pin the
profiles you run most, up to four; the row never wraps, so the panel stays the same height however
many profiles you keep. Click a chip to open the run sheet:

![The run sheet for Incident response, with one entry already active and skipped](images/tutorials/run-profile.png)

- Each entry shows its duration, which you can change for this run.
- Entries that are **already active** or **pending** are skipped, and the sheet says so. An entry
  you are no longer eligible for shows **not eligible · skipped**.
- Roles that need approval are requested and shown as pending afterwards.
- **Start at** schedules the whole run for later.
- Enter a reason and click **Activate N**. Progress shows per row, and a notification reports the
  outcome when the sheet is closed.

**Option-click** a chip to run it without the sheet, using the remembered reason and durations.
If anything needs input, such as a missing reason or an MFA step, the sheet opens instead.

**All N** opens the full list. Type to filter, press Return to run the first match, or Option-Return
to run it silently. The star on each row pins or unpins it; when four are already pinned the footer
says so. Hover a row for **Run** and the **⋯** menu, which is the same menu you get by right-clicking
a chip: run, run with the last reason, pin or unpin, manage, delete.

## Manage profiles

Choose **Manage profiles…** from the All list, or **Edit…** from a chip's menu to open the window
on that profile.

![The Profiles window listing three profiles with Run, Edit and delete controls](images/tutorials/manage-profiles.png)

The list on the left holds every profile; the one you select is edited on the right, and every
change is saved as you make it.

- Rename in the name field. The pin switch puts the profile in the panel row; the shortcut switch
  makes it the profile the global shortcut runs.
- The roles are grouped by account and tenant. Each shows the duration the next run will propose,
  which you can change here, and a **−** button removes it. **Add roles…** opens a picker over every
  account and tenant with search and a kind filter; roles already in the profile are shown greyed.
- **Run…** opens the run sheet, **Delete…** asks first. Active roles are never touched.
- **+** under the list starts an empty profile; drag rows to reorder the chips.

## A global keyboard shortcut

One profile can be bound to a system-wide shortcut, so you can run it without opening the
panel. In **Settings…** under **Global shortcut**:

1. Click **Record shortcut** and press a combination that includes ⌘, ⌃ or ⌥.
2. Choose the profile under **Runs profile**.

The shortcut behaves like Option-clicking the chip: it runs silently when it can and opens the
run sheet when it needs input. If macOS refuses the combination, Elevate says so under the field;
pick another.

## Tips

- Keep profiles small and specific. A profile that includes a role needing approval will always
  leave that role pending; put such roles in their own profile so the rest activate instantly.
- Profiles remember roles by tenant and account. If you sign an account out and back in, its
  profiles keep working; if you remove a tenant, its entries are dropped from every profile.
- Selections can span tenants, and the footer reminds you that switching tabs adds more roles to
  the same selection.
