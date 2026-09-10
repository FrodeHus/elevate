// The callback is display-only. Its URL parameters never authenticate or authorize anyone.
export function describeResult(search) {
  const params = new URLSearchParams(search);
  const adminConsent = (params.get("admin_consent") || "").toLowerCase();
  const error = params.get("error") || "";
  const errorDescription = params.get("error_description") || "";
  const tenant = params.get("tenant") || "";
  const scopes = [
    ...new Set((params.get("scope") || "").split(/\s+/).filter(Boolean)),
  ];
  const result = { state: "empty", tenant, scopes, error, errorDescription };

  // Microsoft also sends admin_consent=True for errors: the flag alone is not success.
  if (params.has("error") || params.has("error_description")) {
    result.state =
      error.toLowerCase() === "access_denied" ||
      /\bAADSTS65004\b/i.test(errorDescription)
        ? "declined"
        : "error";
  } else if (
    ["admin_consent", "tenant", "scope"].some(
      (key) => params.getAll(key).length > 1,
    )
  ) {
    result.state = "incomplete";
  } else if (adminConsent === "false") {
    result.state = "declined";
  } else if (
    adminConsent === "true" &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
      tenant,
    )
  ) {
    result.state = "success";
  } else if (
    ["admin_consent", "tenant", "scope"].some((key) => params.has(key))
  ) {
    result.state = "incomplete";
  }
  return result;
}

const messages = {
  success: {
    title: "Admin consent granted",
    description:
      "Microsoft reported that consent was granted for the shared Elevate app in your tenant.",
    nextTitle: "Next: add your account",
    next: "Switch back to Elevate, choose Add account, and sign in using the shared Elevate app registration. You can close this tab.",
    link: "Read the setup guide",
    href: "https://github.com/FrodeHus/elevate/blob/main/docs/getting-started.md",
    symbol: "✓",
  },
  declined: {
    title: "Consent wasn’t granted",
    description:
      "The consent request was declined or cancelled. This attempt did not complete the setup.",
    nextTitle: "Continue when you’re ready",
    next: "Return to Elevate to start consent again, or ask your tenant administrator to review the requested permissions. You can also use your own app registration.",
    link: "Review the consent guide",
    href: "https://github.com/FrodeHus/elevate/blob/main/docs/shared-app-registration.md",
    symbol: "−",
  },
  error: {
    title: "Consent couldn’t be completed",
    description: "Microsoft returned an error during the consent request.",
    nextTitle: "Check the details before trying again",
    next: "Expand the result details below for the error returned by Microsoft. Check the setup guide or share the error privately with your tenant administrator.",
    link: "Get help with consent",
    href: "https://github.com/FrodeHus/elevate/blob/main/docs/troubleshooting.md",
    symbol: "!",
  },
  incomplete: {
    title: "We couldn’t confirm the result",
    description:
      "The returned consent information is incomplete or unexpected.",
    nextTitle: "Check your setup in Elevate",
    next: "Return to Elevate and try signing in. If setup is not complete, start the consent flow again from the app or ask your tenant administrator to check the permissions in Microsoft Entra.",
    link: "Read the consent guide",
    href: "https://github.com/FrodeHus/elevate/blob/main/docs/shared-app-registration.md",
    symbol: "?",
  },
  empty: {
    title: "No consent result to show",
    description:
      "This page shows the result after an administrator completes the Microsoft consent flow.",
    nextTitle: "Start from Elevate",
    next: "Open Elevate and choose the shared-app setup option to start admin consent. Opening this page directly does not grant any permissions.",
    link: "How to set up the shared app",
    href: "https://github.com/FrodeHus/elevate/blob/main/docs/shared-app-registration.md",
    symbol: "i",
  },
};

const permissionLabels = {
  "User.Read": "Read your basic profile",
  "RoleEligibilitySchedule.Read.Directory": "Read eligible Entra roles",
  "RoleAssignmentSchedule.ReadWrite.Directory":
    "Activate and deactivate Entra roles",
  "RoleManagementPolicy.Read.Directory": "Read Entra activation policies",
  "PrivilegedEligibilitySchedule.Read.AzureADGroup":
    "Read eligible group access",
  "PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup":
    "Activate and deactivate group access",
  "RoleManagementPolicy.Read.AzureADGroup": "Read group activation policies",
  "EntitlementMgmt-SubjectAccess.ReadWrite":
    "Request and manage your access packages",
};

function renderResult() {
  const result = describeResult(window.location.search);
  const message = messages[result.state];
  const setText = (id, text) => {
    document.getElementById(id).textContent = text;
  };
  document.getElementById("consent-card").dataset.state = result.state;
  setText("result-heading", message.title);
  setText("result-description", message.description);
  setText("result-symbol", message.symbol);
  setText("next-heading", message.nextTitle);
  setText("next-description", message.next);
  const guide = document.getElementById("result-guide");
  guide.textContent = message.link;
  guide.href = message.href;
  document.title = `${message.title} — Elevate`;
  document.getElementById("result-details").hidden = result.state === "empty";
  setText("tenant-value", result.tenant || "Not included in the response");
  const errorSection = document.getElementById("error-details");
  errorSection.hidden = !(result.error || result.errorDescription);
  setText("error-code", result.error || "No error code returned");
  setText(
    "error-description",
    result.errorDescription || "No additional description returned.",
  );
  document.getElementById("scope-details").hidden = result.state !== "success";
  setText(
    "scope-summary",
    result.scopes.length
      ? `Permissions returned by Microsoft (${result.scopes.length})`
      : "No permission list was included in the response.",
  );
  const list = document.getElementById("scope-list");
  for (const scope of result.scopes) {
    const item = document.createElement("li");
    const name = scope.replace(/^https:\/\/graph\.microsoft\.com\//, "");
    const label = document.createElement("span");
    label.textContent =
      permissionLabels[name] || "Additional returned permission";
    const code = document.createElement("code");
    code.textContent = scope;
    item.append(label, code);
    list.append(item);
  }
  const copy = document.getElementById("copy-tenant");
  if (result.tenant && navigator.clipboard && window.isSecureContext) {
    copy.hidden = false;
    copy.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(result.tenant);
        setText("copy-feedback", "Tenant ID copied.");
      } catch {
        setText(
          "copy-feedback",
          "Copy unavailable. Select the tenant ID and copy it manually.",
        );
      }
    });
  }
}

if (typeof document !== "undefined") renderResult();
