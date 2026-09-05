#!/usr/bin/env bash
# Creates the Elevate app registration with Azure CLI and prints the client id.
# Usage: ./create-app-registration.sh [display-name] [bundle-id]
#   display-name  defaults to "Elevate"
#   bundle-id     defaults to no.reothor.elevate (must match the app's bundle id; only change it for your own build)
# Requires: az login as a user allowed to create app registrations (Application Developer or above).
set -euo pipefail
NAME="${1:-Elevate}"
BUNDLE="${2:-no.reothor.elevate}"
HERE="$(cd "$(dirname "$0")" && pwd)"

APP_ID=$(az ad app create \
  --display-name "$NAME" \
  --sign-in-audience AzureADMultipleOrgs \
  --public-client-redirect-uris "msauth.${BUNDLE}://auth" "http://localhost" \
  --web-redirect-uris "https://login.microsoftonline.com/common/oauth2/nativeclient" \
  --required-resource-accesses "@${HERE}/required-resource-access.json" \
  --query appId -o tsv)

# A service principal in the home tenant so admin consent can be recorded there.
az ad sp create --id "$APP_ID" >/dev/null 2>&1 || true

echo "Application (client) ID: $APP_ID"
echo
echo "Next: grant admin consent in each tenant that will use Elevate:"
echo "  az ad app permission admin-consent --id $APP_ID      (home tenant, needs Privileged Role Administrator or Global Administrator)"
echo "  or open: https://login.microsoftonline.com/{tenant-id}/adminconsent?client_id=$APP_ID"
echo "Then paste the client id into Elevate → Settings."
