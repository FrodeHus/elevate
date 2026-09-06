# Security policy

Elevate handles privileged access: it signs in to Microsoft Entra, holds tokens in your keychain and
activates PIM roles. Security reports are taken seriously — please report them privately.

## Reporting a vulnerability

Use GitHub's private reporting:
[Report a vulnerability](https://github.com/FrodeHus/elevate/security/advisories/new). Private
vulnerability reporting is enabled on this repository, so no email address is needed; if the form
is unavailable to you, open a regular issue that only asks for a private channel, without details.

Please do **not** open a public issue for a vulnerability, and do not include real tokens, client
ids, tenant ids or account names in the report — a redacted transcript is enough.

Include, as far as you can: what you did, what happened, what you expected, the Elevate version, and
the macOS version.

## Supported versions

Only the [latest release](https://github.com/FrodeHus/elevate/releases/latest) is supported. Fixes
ship in a new release; older versions receive no backports.

## What counts

In scope, roughly in order of severity:

- Token handling — a token or refresh token leaking into logs, diagnostics, disk, the pasteboard or
  a network request it does not belong in.
- Keychain use — items stored too broadly, without the "this device only" protection, or readable by
  other applications when they should not be.
- Authentication and authorization flows — the loopback redirect listener, PKCE, MSAL integration,
  redirect-URI or state handling, tenant/account mix-ups.
- Anything that could escalate privileges or leak privileged access — activating a role the user is
  not eligible for, acting in the wrong tenant or as the wrong account, or exposing which roles and
  approvals a user holds.
- The release and distribution path — the workflow, the DMG, the Homebrew cask.

Out of scope: findings that require an already-compromised Mac or an attacker with your unlocked
login keychain, behaviour of Microsoft's own services, Conditional Access policy decisions, and the
fact that current releases are ad-hoc signed rather than notarized (a known, documented state — see
[docs/releasing.md](docs/releasing.md)).

## What to expect

- An acknowledgement within one week of the report.
- Updates as the fix progresses, and credit in the release notes and
  [CHANGELOG.md](CHANGELOG.md) if you would like it.
- Public disclosure once a fixed release is out.

There is no bug bounty: Elevate is an unfunded open-source project, so reports are rewarded with
thanks and credit only.
