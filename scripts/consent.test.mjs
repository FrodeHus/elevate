import test from "node:test";
import assert from "node:assert/strict";
import { describeResult } from "../site/consent.mjs";
const tenant = "11111111-2222-3333-4444-555555555555";

test("Microsoft error takes precedence over admin_consent=True", () => {
  assert.equal(
    describeResult("?admin_consent=True&error=server_error").state,
    "error",
  );
});
test("denied consent is distinct from a technical failure, even with the flow flag", () => {
  assert.equal(
    describeResult(
      "?admin_consent=True&error=consent_required&error_description=AADSTS65004%3A+denied",
    ).state,
    "declined",
  );
  assert.equal(describeResult("?error=access_denied").state, "declined");
  assert.equal(describeResult("?admin_consent=False").state, "declined");
});
test("valid success retains only the permissions actually returned", () => {
  const result = describeResult(
    `?admin_consent=True&tenant=${tenant}&scope=https%3A%2F%2Fgraph.microsoft.com%2FUser.Read`,
  );
  assert.equal(result.state, "success");
  assert.equal(result.tenant, tenant);
  assert.deepEqual(result.scopes, ["https://graph.microsoft.com/User.Read"]);
  assert.deepEqual(
    describeResult(`?admin_consent=true&tenant=${tenant}`).scopes,
    [],
  );
});
test("missing or invalid tenant cannot produce a complete success result", () => {
  assert.equal(describeResult("?admin_consent=True").state, "incomplete");
  assert.equal(
    describeResult("?admin_consent=True&tenant=not-a-tenant").state,
    "incomplete",
  );
});
test("empty error values and descriptions cannot fall through to success", () => {
  assert.equal(
    describeResult(`?admin_consent=True&tenant=${tenant}&error=`).state,
    "error",
  );
  assert.equal(
    describeResult(
      `?admin_consent=True&tenant=${tenant}&error_description=failed`,
    ).state,
    "error",
  );
});
test("direct visits are neutral and ambiguous parameters are incomplete", () => {
  assert.equal(describeResult("").state, "empty");
  assert.equal(describeResult("?utm_source=docs").state, "empty");
  assert.equal(
    describeResult(`?admin_consent=True&admin_consent=False&tenant=${tenant}`)
      .state,
    "incomplete",
  );
  assert.equal(describeResult("?admin_consent=unexpected").state, "incomplete");
});
test("untrusted descriptions remain plain data and scope lists are deduplicated", () => {
  assert.equal(
    describeResult(
      "?error=oops&error_description=%3Cimg%20onerror%3Dalert(1)%3E",
    ).errorDescription,
    "<img onerror=alert(1)>",
  );
  assert.deepEqual(
    describeResult(
      `?admin_consent=True&tenant=${tenant}&scope=User.Read+User.Read`,
    ).scopes,
    ["User.Read"],
  );
});
