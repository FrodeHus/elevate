# Audit report redesign — executive summary, areas, roll-ups and group nesting

Date: 2026-09-14. Extends `2026-09-13-elevate-audit-design.md` §6.3 (the HTML
report). Terminal and JSON output are untouched; the JSON field names remain
the stability contract. Mockups: the "Audit Report Redesign" design canvas
(desktop and phone artboards, sample Contoso data).

## 1. Goal

The current `--html` report opens with four severity counts and then one table
per rule. It answers "what did the tool find" but not "how is this tenant
doing", and a tenant with a role-assigned group of several hundred members
produces several hundred near-identical rows.

The redesigned report:

- opens with an **executive summary**: one verdict sentence and one tile per
  area, each linking to its section;
- organises findings by **area**, with collapsible areas and rule blocks;
- **rolls up** per-member findings under the group that grants the access, so
  the page stays readable at thousands of findings;
- shows **how groups nest** for each rolled-up group, as an outline and as a
  small diagram, both generated at render time;
- adds search, severity filtering and expand/collapse as **progressive
  enhancement**: the page is complete and readable with JavaScript off.

Decisions made during brainstorming: progressive enhancement rather than a
JavaScript-rendered page or a strict no-script rule; a plain-sentence verdict
with no score, grade or traffic light; sections by area and rule only, no
"by person" view in this cut.

## 2. Areas

Every finding belongs to exactly one area, decided by its rule code:

| Area | Rule codes | Section id |
|---|---|---|
| Entra roles | `ENTRA-USER-PERMANENT`, `ENTRA-GROUP-PERMANENT`, `ENTRA-GROUP-NOT-PIM`, `ENTRA-GROUP-NOT-ASSIGNABLE` | `entra` |
| PIM for Groups | `GROUP-MEMBER-PERMANENT` | `pim-groups` |
| Azure RBAC | `AZURE-PERMANENT` | `azure` |
| Guests | `GUEST-PERMANENT` | `guests` |
| Workload identities | `SP-PERMANENT` | `workload` |
| Hygiene | `ELIGIBLE-NO-END`, `GA-COUNT` | `hygiene` |
| Coverage | none; built from `Skipped` | `coverage` |

A rule code the renderer does not know (a future rule) lands in an "Other"
area (`other`), rendered last before Coverage, so a new rule never disappears
from the report. Areas are listed in the table's order.

Skipped sources affect areas as follows:

| Skipped source | Effect |
|---|---|
| `azure` | Azure RBAC is **Not scanned** |
| `pim-for-groups` | PIM for Groups is **Not scanned** |
| `azure-management-groups` | Azure RBAC is marked **under-counts** |
| `groups` | Entra roles and Guests are marked **under-counts** |
| `principals` | no area marking; the reason appears in Coverage only |

"Not scanned" wins over any finding-derived state. "Under-counts" is an extra
marker beside the state, on the tile and on the section heading.

## 3. Executive summary

The header keeps the eyebrow, tenant name and the four meta items. The
severity cards are removed. Below the meta:

**Verdict.** One or two sentences, 22px on desktop:

- Count distinct principals across the standing-access findings
  (`ENTRA-USER-PERMANENT`, the per-person `ENTRA-GROUP-PERMANENT` findings,
  `GROUP-MEMBER-PERMANENT`, `AZURE-PERMANENT`, `GUEST-PERMANENT`,
  `SP-PERMANENT`), by principal id, split into people (users, guests included)
  and workload identities (service principals). Group-principal findings and
  the hygiene rules do not count. Wording: "**14 people and 2 workload
  identities hold standing privileged access** in Contoso." Singular forms
  and a missing half ("14 people hold …") are handled. Zero of both: "**No
  standing privileged access was found** in Contoso."
- If any source was skipped, a second sentence: "Azure management groups were
  not scanned, so the Azure section under-counts." One clause per distinct
  skipped source, using this table of phrasings:

  | Source | Clause |
  |---|---|
  | `azure` | Azure was not scanned |
  | `azure-management-groups` | Azure management groups were not scanned, so the Azure section under-counts |
  | `pim-for-groups` | PIM for Groups was not scanned |
  | `groups` | some groups could not be read, so the Entra and Guests sections under-count |
  | `principals` | some principals could not be resolved to names |

- The `--min-severity` hidden-count note stays, under the verdict.

**Tiles.** A grid (`repeat(auto-fit, minmax(220px, 1fr))`) of one tile per
area in the order of §2, Other included only when non-empty, Coverage always.
Each tile is an `<a href="#<section id>">` containing:

- the area name;
- a state pill: **Critical** (any High), **Attention** (any Medium or Low, no
  High), **Review** (Info only), **Clean** (no findings), **Not scanned**
  (per §2). Coverage's pill is **Complete** when nothing was skipped and
  **N skipped** otherwise, styled as Attention;
- the non-zero severity counts for the area, large number plus severity word;
  Coverage shows the skipped count with the word "skipped";
- one sentence per area, from these templates (numbers are distinct
  principals unless stated):
  - Entra roles: "{n} people hold a permanent role directly. {g} groups grant
    roles to {m} more." Each half only when non-zero; Clean: "No permanent
    Entra role assignments."
  - PIM for Groups: "{n} permanent members remain in groups PIM already
    manages." Clean: "Every PIM-managed group has only eligible members."
  - Azure RBAC: "{n} permanent privileged assignments across {s} scopes."
    Clean: "No permanent privileged Azure assignments."
  - Guests: "{n} guests hold a permanent privileged role." Clean: "No guest
    holds a permanent privileged role."
  - Workload identities: "{n} service principals hold permanent roles. Review
    whether they need them." Clean: "No service principal holds a permanent
    privileged role."
  - Hygiene: "{n} eligibilities never expire." plus, when `GA-COUNT` fired,
    "{k} people can become Global Administrator." Clean: "Eligibilities expire
    and the Global Administrator count is within range."
  - Coverage: the skipped reasons joined, or "Every source was read."
  - Not scanned tiles show the skipped reason instead.

The verdict and the tile counts use the unfiltered `Summary` semantics: they are computed
from every finding, not the `--min-severity`-filtered list, matching the
existing rule that the summary is never filtered. Sections below render the
filtered list, as today.

## 4. Body structure

```
<main>
  toolbar               (script-only, see §6)
  Start here            (High findings, rolled up, see §5)
  <details class="area" id="entra" open>   one per area
    <summary> caret · h2 · severity pills · under-counts marker </summary>
    <p> one-line area description </p>
    <details class="rule" id="entra-group-permanent-high" open>  one per (rule, severity)
      <summary> caret · rule code · severity pill · description · count </summary>
      group cards, or a findings table
    </details>
  </details>
  Coverage              (a table: source, state, what it means)
  Appendix              (as today, inside <details>)
</main>
```

- Areas with any High finding open by default; the rest are closed. Every
  rule block is open by default inside its area. Coverage is open when
  anything was skipped.
- Section ids: areas as in §2; rule blocks keep today's `<code>-<severity>`
  ids, which existing tests pin. Tile anchors target the area's `<summary>`
  (id on the summary element) so the target is visible even when the area
  is closed. The script also opens the targeted area (§6).
- The area description lines: Entra roles "Permanent active directory role
  assignments, held directly or through groups."; PIM for Groups "Groups PIM
  already manages that still have permanent members or owners."; Azure RBAC
  "Permanent privileged Azure role assignments at any scope."; Guests "Guests
  holding a permanent privileged role, directly or through a group.";
  Workload identities "Service principals and managed identities holding
  permanent privileged roles; PIM eligibility does not apply."; Hygiene
  "Eligibilities without an end date and the Global Administrator count.";
  Other "Findings from rules this renderer does not know."
- Coverage table rows, one per known source in fixed order (Entra role
  assignments, Groups, PIM for Groups, Azure subscriptions, Azure management
  groups, Principals): state pill **read** or **skipped**, and the skipped
  reason(s) or a blank cell. A source that was `--skip-azure`d reads
  "skipped with --skip-azure" as today.
- Rule blocks that are not rolled up keep today's table (Principal, Role,
  Scope, How, Remedy) with the "through N groups" `<details>` in How.

## 5. Roll-ups

Roll-ups are a rendering concern of `HtmlRenderer` only; `Finding` and the
JSON do not change.

**Which findings roll up.** `ENTRA-GROUP-PERMANENT` and
`GROUP-MEMBER-PERMANENT`. For `ENTRA-GROUP-PERMANENT` the key is (group
principal id, role id, scope id): the group-level finding (principal type
Group, empty `Via`) is the card, and every finding with the same rule, role
and scope whose `Via[0].Id` equals the group id is a member of that card. For
`GROUP-MEMBER-PERMANENT` the key is (scope id, role id), the scope being the
PIM-managed group; there is no group-level finding, so the card is
synthesised from the first member's role and scope. A per-person finding
whose card key has no group finding (possible when `--min-severity` or
`--ignore` removed it) still renders under a synthesised card, so nothing is
dropped.

**Card.** Title line: group name, `group` pill, "grants", role name, "to N
people through M nested groups" (M = distinct groups in the members' `Via`
lists minus the top group; "directly" when M is 0). Then the card's remedy
and portal link (from the group finding; for synthesised cards the first
member's). Then a two-column body:

- **Membership outline** (`<ul class="tree">`, nested lists): the role as
  the root, the top group, and each nested group as a node with its person
  count. A group with 10 or fewer direct people lists them inline (name,
  `guest` pill where applicable); a larger group shows "N people, listed
  below". The outline is a trie built from the members' `Via` lists: node
  identity is the group id, order is first-seen.
- **Nesting diagram** (inline SVG): the same trie without people, laid out
  left to right in tiers (depth = tier), each node a 140×46 rounded rect
  with the group name (ellipsised past 18 characters, full name in a
  `<title>`) and "N people" beneath; the role is the first node, styled as
  the High pill colours; edges are cubic curves in `--line`. Tier x spacing
  180px, node y spacing 66px; the SVG's `viewBox` fits the content and the
  element is `max-width:100%; height:auto`. The diagram is omitted when the
  trie has more than 12 group nodes (the outline carries the structure) or
  when the group grants directly with no nested groups. Hidden below 760px
  by CSS.
- **People reached** table (`<details>` under the body, open): Person (name,
  UPN, guest pill), Through (the `Via` names joined with " ← "), Remedy with
  portal link. This is where every member appears regardless of outline
  truncation.

**Start here** lists High findings in report order, one item per card for
rolled-up rules and one per finding otherwise, with the count "N people
through M groups" on card items. Existing per-item wording stays for direct
findings.

**Unreadable nested groups** are not represented as nodes (the collector
records only a count). When the `groups` source is skipped with the
"nested group(s) could not be read" reason, each card shows one line under
the title: "Some nested groups could not be read; their members are missing."

## 6. Progressive enhancement

One inline `<script>` at the end of `<body>`, no external requests, no
frameworks, under roughly 150 lines. It only hides, reveals, opens and
closes; it never creates content the no-script page lacks, except the
toolbar itself. With scripts off: no toolbar, every table row visible, every
`<details>` in its default state.

The script:

1. **Toolbar**, inserted before Start here, `position: sticky; top: 12px`:
   a search input, four severity chips (High and Medium on by default, Low
   and Info off; the off ones hide rule blocks and Start-here items of that
   severity), and Expand all / Collapse all.
2. **Search** matches case-insensitively against a `data-search` attribute
   the renderer writes on every findings row, card and Start-here item
   (lower-cased principal name, UPN, role, scope and `Via` names joined by
   spaces). Non-matching rows, cards and items get `hidden`; a rule block
   whose rows are all hidden gets `hidden`; an area whose blocks are all
   hidden shows a "No findings match." line instead of collapsing.
   Searching opens every area and block that has a match.
3. **Caps.** Any table or list with more than 50 rows gets rows beyond 50
   hidden and a footer "Showing 50 of N · Show all" that reveals them; Start
   here caps at 10 with "Show all N actions". Search ignores caps (all
   matching rows are shown).
4. **Anchors.** On load and on `hashchange`, if the hash names an area
   summary, open that area's `<details>`.
5. **Print** keeps today's behaviour (open every `<details>`) and also
   removes caps and hides the toolbar (`@media print` hides `.toolbar`).

The chips and search state are not persisted.

## 7. Styling

`report.css` gains the tile, toolbar, chip, area, tree, group-card and
diagram rules from the mockup, using only the existing tokens plus one
green pair for **Clean** and **read** (`#dcfae6` / `#067647`, in the same
family as the existing red and amber). The `:root` token block stays
byte-identical to `site/styles.css`; that test continues to pass. Phone
layout is CSS only: tiles stack via the auto-fit grid, the diagram is
hidden, chips wrap, the group-card body becomes one column.

## 8. Components and files

- `Rendering/ReportAreas.cs` (new): the rule-code-to-area map, area
  metadata (id, name, description line), tile state, per-area sentence
  templates, verdict sentence, and the skipped-source effects of §2. Pure
  functions over `AuditReport`; unit-tested directly.
- `Rendering/GroupRollup.cs` (new): builds cards from findings (§5 keys),
  the `Via` trie, outline rendering and the SVG layout. Pure; unit-tested.
- `Rendering/HtmlRenderer.cs`: restructured per §4, calling the two above;
  the escaping helper and the appendix stay.
- `Rendering/report.css`: §7. `Rendering/report.js` (new embedded resource,
  `Elevate.Audit.Resources.report.js`): §6, inlined by the renderer.
- `site/audit-sample.html`: regenerated golden. The sample snapshot in
  `Tests/Support/SampleSnapshot` gets a second level of nesting under Tier 0
  Admins so the diagram and outline render in the sample.
- `docs/audit.md` §6 "Reading the report": rewritten for the new structure.

## 9. Testing

- `ReportAreasTests`: every rule code maps to an area; unknown code maps to
  Other; tile state for each combination (High, Medium only, Info only,
  none, skipped); verdict wording for plural, singular, one-half and zero
  cases; skipped-source clauses.
- `GroupRollupTests`: a fixture built in code with one role-assigned group,
  three nested groups (one shared by two parents, to pin first-seen
  identity), 120 members: card count, people and nested-group counts,
  outline truncation at 10, diagram present under 12 groups and absent
  above, member table lists all 120, synthesised card when the group finding
  is filtered out.
- `HtmlRendererTests`: existing tests kept (unique ids, one h1, https or
  fragment links only, rule-block ids, hidden-count note); new assertions
  that every `data-search` value is lower-case, that the script tag is
  inline, that a no-script rendering contains every member row (no `hidden`
  attribute in the markup), that tile anchors resolve to existing ids, and
  the regenerated golden.
- Token block equality with `site/styles.css`: unchanged.

## 10. Out of scope

A "by person" view; a tenant-wide group graph; persisted filter state;
representing unreadable groups as nodes (needs the collector to record ids);
dark mode (the site defines none).
