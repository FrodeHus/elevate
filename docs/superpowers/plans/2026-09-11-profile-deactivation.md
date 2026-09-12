# Profile deactivation implementation plan

Goal: implement issue #132 on macOS and Windows.

Architecture: Persist immutable profile-run snapshots in state.json. Each entry owns an exact provider schedule, never a current profile role list. Revalidate provider assignments before deactivation. Record confirmed completions independently so retries and restarts cannot repeat them. Extend native status playback with downward motion and actual-result states.

- [x] Add stable schedule correlation to provider responses and targeted deactivation requests; test request/instance identity and response confirmation.
- [x] Add backward-compatible profileRuns storage and pure assignment matching/planning tests for expiry, replacement, completion, minimum period, and serialization.
- [x] Capture each successful profile result, exclude pre-existing/in-flight roles at execution, and implement per-run bulk deactivation with fresh provider validation and per-entry retries.
- [x] Add run selection and per-role statuses on both platforms, shared-profile impact captions, and downward animation for bulk and single deactivation.
- [x] Run macOS core/app tests, Windows cross-platform core/model tests, inspect final diff, and document platform build limits.

Platform work proceeds independently under the subagent-driven-development skill. Provider model changes are owned by one shared task to keep JSON fields aligned. Existing user authorization covers implementation; no additional design approval is required.

Verification: macOS core passed 342 tests, the macOS app build passed, and the affected app suites passed 23 tests. Windows core passed 426 tests and the model suite passed 111 tests. Native WinUI XAML compilation cannot run on this macOS host. The full macOS app suite still has 15 unsigned-host issues in access-package and silent-sign-in tests; a representative access-package failure was reproduced in an untouched HEAD archive.

Safety ruling: a pending activation without a verifiable original interval is blocked for bulk deactivation and directs the user to the individual role action. Matching it by schedule alone could remove a later extension, so no interval is inferred.
