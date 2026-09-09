#!/usr/bin/env python3
"""Validate the enterprise kit against docs/enterprise/keys.md.

`docs/enterprise/keys.md` is the single source of truth for the managed
configuration keys: its one key table is parsed, and every template in
`enterprise/` has to carry exactly that set of keys, in the syntax its format
uses. The profile documents found in those templates are checked against the
shape rules of the design's §7.1 (a re-implementation of `ManagedProfileSet`),
and the example values against the rules `ManagedConfiguration` applies.

Standard library only, so CI needs nothing but python3. Exits 0 with a summary
line, or prints one `error: …` line per problem and exits 1.
"""

from __future__ import annotations

import argparse
import json
import plistlib
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ADMX_NS = "{http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions}"
PREFERENCE_DOMAIN = "no.reothor.elevate"
REGISTRY_PATH = r"Software\Policies\Reothor\Elevate"
SIGN_IN_METHODS = {"ownApp", "azureCLI", "azurePowerShell", "custom"}
ROLE_KINDS = {"entraDirectory", "azureResource", "group"}
GROUP_ACCESS = {"member", "owner"}
LIST_KEYS = ("AllowedSignInMethods", "AllowedTenants", "PinnedTenants")
# The seven managed configuration keys the spec defines (design §2). keys.md is
# the source of truth for their documentation, but the set itself is fixed.
EXPECTED_KEYS = {
    "ClientId",
    "DisableUpdateCheck",
    "AllowedSignInMethods",
    "AllowedTenants",
    "PinnedTenants",
    "ManagedProfiles",
    "ManagedProfilesUrl",
}
GUID_RE = re.compile(r"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\Z")
SLUG_RE = re.compile(r"\A[a-z0-9-]{1,64}\Z")
# `PnDTnHnMnS`, the shape ISO8601Duration.parse accepts.
DURATION_RE = re.compile(r"\AP(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?)?\Z")
# A key table row: the first cell is a backticked key name.
KEY_ROW_RE = re.compile(r"^\|\s*`([A-Za-z][A-Za-z0-9]*)`\s*\|(.*)\|\s*$")

REPO_URL_PREFIX = "https://github.com/FrodeHus/elevate/"
URL_RE = re.compile(r"https?://[^\s\"'<>)\\]+")
# Boilerplate structural URIs (XML namespaces, plist/ADMX schema references, the
# JSON Schema meta-schema) are not links a reader would follow to the repo, so
# they are not held to the "must point at our repo" rule.
BOILERPLATE_URL_PREFIXES = (
    "http://www.w3.org/",
    "http://schemas.microsoft.com/GroupPolicy/",
    "http://www.apple.com/DTDs/",
    "http://json-schema.org/",
)
# Placeholder hosts the kit intentionally uses in examples.
ALLOWED_URL_PREFIXES = ("https://example.com/", "https://login.microsoftonline.com/")


class Errors:
    """Collects failures so one run reports every problem, not just the first."""

    def __init__(self) -> None:
        self.messages: list[str] = []

    def add(self, message: str) -> None:
        self.messages.append(message)

    def __bool__(self) -> bool:
        return bool(self.messages)


# --------------------------------------------------------------------------- loading


def read_text(path: Path, errors: Errors) -> str | None:
    try:
        return path.read_text(encoding="utf-8")
    except OSError as exc:
        errors.add(f"{path}: cannot be read ({exc.strerror})")
        return None


def load_json(path: Path, errors: Errors) -> object | None:
    text = read_text(path, errors)
    if text is None:
        return None
    try:
        return json.loads(text)
    except json.JSONDecodeError as exc:
        errors.add(f"{path}: is not valid JSON ({exc.msg} at line {exc.lineno})")
        return None


def load_xml(path: Path, errors: Errors) -> ET.Element | None:
    text = read_text(path, errors)
    if text is None:
        return None
    try:
        return ET.fromstring(text)
    except ET.ParseError as exc:
        errors.add(f"{path}: is not well-formed XML ({exc})")
        return None


def load_plist(path: Path, errors: Errors) -> object | None:
    try:
        with path.open("rb") as handle:
            return plistlib.load(handle)
    except OSError as exc:
        errors.add(f"{path}: cannot be read ({exc.strerror})")
    except Exception as exc:  # plistlib raises a variety of parse errors
        errors.add(f"{path}: is not a valid property list ({exc})")
    return None


# --------------------------------------------------------------------------- keys.md


def parse_keys(path: Path, errors: Errors) -> list[str]:
    """The key names in the one table of keys.md, in document order."""
    text = read_text(path, errors)
    if text is None:
        return []
    keys: list[str] = []
    for line in text.splitlines():
        match = KEY_ROW_RE.match(line)
        if not match:
            continue
        name = match.group(1)
        cells = [cell.strip() for cell in match.group(2).split("|")]
        if len(cells) < 5:
            errors.add(f"{path}: row for `{name}` has {len(cells) + 1} columns, expected 6")
            continue
        if name in keys:
            errors.add(f"{path}: key `{name}` is listed twice")
            continue
        if not cells[0]:
            errors.add(f"{path}: row for `{name}` has an empty Type cell")
        keys.append(name)
    if not keys:
        errors.add(f"{path}: no key table rows found (expected rows starting with | `Key` |)")
        return keys
    if set(keys) != EXPECTED_KEYS:
        missing = sorted(EXPECTED_KEYS - set(keys))
        extra = sorted(set(keys) - EXPECTED_KEYS)
        detail = []
        if missing:
            detail.append(f"missing {missing}")
        if extra:
            detail.append(f"unexpected {extra}")
        errors.add(f"{path}: key table does not list exactly the seven managed keys ({', '.join(detail)})")
    return keys


# --------------------------------------------------------------------------- §7.1 profile sets


def validate_profile_set(document: object, where: str, errors: Errors) -> None:
    """Re-implements ManagedProfileSet.parse's shape rules (design §7.1)."""
    if not isinstance(document, dict):
        errors.add(f"{where}: profile set is not a JSON object")
        return

    version = document.get("version")
    if version is None:
        errors.add(f"{where}: profile set has no 'version' (ManagedProfileSet.parse requires it)")
    elif isinstance(version, bool) or not isinstance(version, (int, float)) or version != 1:
        errors.add(f"{where}: profile set version {version!r} is not supported (expected 1)")

    profiles = document.get("profiles", [])
    if not isinstance(profiles, list):
        errors.add(f"{where}: 'profiles' is not an array")
        return

    seen: set[str] = set()
    for index, profile in enumerate(profiles, start=1):
        if not isinstance(profile, dict):
            errors.add(f"{where}: profile {index}: not a JSON object")
            continue
        identifier = profile.get("id")
        if not isinstance(identifier, str) or not identifier:
            errors.add(f"{where}: profile {index}: id is required")
            continue
        if not SLUG_RE.match(identifier):
            errors.add(f"{where}: profile '{identifier}': id must match [a-z0-9-]{{1,64}}")
        if identifier in seen:
            errors.add(f"{where}: profile '{identifier}': duplicate id")
        seen.add(identifier)
        name = profile.get("name")
        if not isinstance(name, str) or not name:
            errors.add(f"{where}: profile '{identifier}': name is required")
        if "pinned" in profile and not isinstance(profile["pinned"], bool):
            errors.add(f"{where}: profile '{identifier}': pinned must be a boolean")
        if "reason" in profile and not isinstance(profile["reason"], str):
            errors.add(f"{where}: profile '{identifier}': reason must be a string")

        roles = profile.get("roles", [])
        if not isinstance(roles, list):
            errors.add(f"{where}: profile '{identifier}': roles is not an array")
            continue
        for role_index, role in enumerate(roles, start=1):
            validate_role(role, f"{where}: profile '{identifier}' role {role_index}", errors)


def validate_role(role: object, where: str, errors: Errors) -> None:
    if not isinstance(role, dict):
        errors.add(f"{where}: not a JSON object")
        return

    def text(field: str) -> str | None:
        value = role.get(field)
        return value if isinstance(value, str) and value else None

    kind = role.get("kind")
    if not isinstance(kind, str):
        errors.add(f"{where}: kind is required")
        return
    if kind not in ROLE_KINDS:
        errors.add(f"{where}: unknown kind '{kind}'")
        return
    if not text("tenant"):
        errors.add(f"{where}: tenant is required")

    duration = role.get("duration")
    if duration is not None:
        if not isinstance(duration, str) or not DURATION_RE.match(duration) or duration == "P":
            errors.add(f"{where}: '{duration}' is not a valid duration")

    access = role.get("access")
    if access is not None and access not in GROUP_ACCESS:
        errors.add(f"{where}: unknown access '{access}'")

    if kind == "entraDirectory" and not text("role"):
        errors.add(f"{where}: role is required")
    if kind == "azureResource":
        if not text("role"):
            errors.add(f"{where}: role is required")
        if not text("scope"):
            errors.add(f"{where}: scope is required for azureResource")
    if kind == "group" and not text("group"):
        errors.add(f"{where}: group is required")


def check_profiles_value(value: object, where: str, errors: Errors) -> None:
    """A ManagedProfiles value is the document itself or a string holding it."""
    if isinstance(value, str):
        try:
            document = json.loads(value)
        except json.JSONDecodeError as exc:
            errors.add(f"{where}: ManagedProfiles is not valid JSON ({exc.msg} at position {exc.pos})")
            return
        validate_profile_set(document, where, errors)
    elif isinstance(value, dict):
        validate_profile_set(value, where, errors)
    else:
        errors.add(f"{where}: ManagedProfiles must be a JSON object or a string holding one")


# --------------------------------------------------------------- ManagedConfiguration value rules


def check_values(values: dict, where: str, errors: Errors, *, placeholders_allowed: bool) -> None:
    """The rules ManagedConfiguration.load applies to each value it reads."""
    client_id = values.get("ClientId")
    if client_id is not None:
        if not isinstance(client_id, str) or not GUID_RE.match(client_id):
            errors.add(f"{where}: ClientId '{client_id}' is not a GUID")
        elif not placeholders_allowed and client_id == "00000000-0000-0000-0000-000000000000":
            errors.add(f"{where}: ClientId is the all-zero GUID, which the app rejects")

    disable = values.get("DisableUpdateCheck")
    if disable is not None and not isinstance(disable, bool):
        errors.add(f"{where}: DisableUpdateCheck must be a boolean, not {type(disable).__name__}")

    # An absent or empty array is legal — it means no restriction (spec §2).
    methods = values.get("AllowedSignInMethods")
    if methods is not None:
        if not isinstance(methods, list):
            errors.add(f"{where}: AllowedSignInMethods must be an array")
        else:
            for method in methods:
                if method not in SIGN_IN_METHODS:
                    errors.add(f"{where}: AllowedSignInMethods: unknown method '{method}'")

    for key in ("AllowedTenants", "PinnedTenants"):
        tenants = values.get(key)
        if tenants is None:
            continue
        if not isinstance(tenants, list):
            errors.add(f"{where}: {key} must be an array")
            continue
        for tenant in tenants:
            if not isinstance(tenant, str) or not tenant.strip():
                errors.add(f"{where}: {key} contains a blank entry")

    url = values.get("ManagedProfilesUrl")
    if url is not None and (not isinstance(url, str) or not url.startswith("https://")):
        errors.add(f"{where}: ManagedProfilesUrl '{url}' is not an https URL")

    if "ManagedProfiles" in values:
        check_profiles_value(values["ManagedProfiles"], where, errors)


def check_key_set(present: list[str], keys: list[str], where: str, errors: Errors) -> None:
    missing = [key for key in keys if key not in present]
    extra = [key for key in present if key not in keys]
    for key in missing:
        errors.add(f"{where}: key '{key}' is missing")
    for key in extra:
        errors.add(f"{where}: key '{key}' is not in the key reference")


# --------------------------------------------------------------------------- links


def check_urls(kit: Path, errors: Errors) -> None:
    """Every http(s) URL in the kit has to point at our own repository, except
    the boilerplate schema/namespace URIs and the documented placeholder hosts."""
    for path in sorted(p for p in kit.rglob("*") if p.is_file()):
        text = path.read_text(encoding="utf-8", errors="ignore")
        for url in URL_RE.findall(text):
            url = url.rstrip(".,;")
            if url.startswith(BOILERPLATE_URL_PREFIXES) or url.startswith(ALLOWED_URL_PREFIXES):
                continue
            if not url.startswith(REPO_URL_PREFIX):
                errors.add(f"{path}: URL '{url}' does not point at {REPO_URL_PREFIX}")


# --------------------------------------------------------------------------- ADMX/ADML


def check_admx(admx_path: Path, adml_path: Path, keys: list[str], errors: Errors) -> None:
    root = load_xml(admx_path, errors)
    adml = load_xml(adml_path, errors)
    if root is None or adml is None:
        return

    target = root.find(f"{ADMX_NS}policyNamespaces/{ADMX_NS}target")
    if target is None or target.get("namespace") != "Reothor.Elevate":
        errors.add(f"{admx_path}: policyNamespaces target namespace must be Reothor.Elevate")

    strings = {node.get("id") for node in adml.iter(f"{ADMX_NS}string")}
    presentations = {node.get("id") for node in adml.iter(f"{ADMX_NS}presentation")}

    # Every $(string.X) / $(presentation.X) anywhere in the ADMX must resolve in the ADML.
    for node in root.iter():
        for attribute, value in node.attrib.items():
            for kind, name in re.findall(r"\$\((string|presentation)\.([^)]+)\)", value or ""):
                table = strings if kind == "string" else presentations
                if name not in table:
                    errors.add(
                        f"{adml_path}: no {kind} '{name}' for {node.tag.split('}')[-1]}"
                        f"/@{attribute} in {admx_path.name}"
                    )

    supported = {node.get("name") for node in root.iter(f"{ADMX_NS}definition")}
    if "SUPPORTED_Elevate_1_6" not in supported:
        errors.add(f"{admx_path}: supportedOn definition SUPPORTED_Elevate_1_6 is missing")

    policies = list(root.iter(f"{ADMX_NS}policy"))
    check_key_set([policy.get("name") for policy in policies], keys, f"{admx_path}", errors)

    for policy in policies:
        name = policy.get("name")
        if policy.get("class") != "Both":
            errors.add(f"{admx_path}: policy '{name}' must have class=\"Both\"")
        if policy.get("key") != REGISTRY_PATH:
            errors.add(f"{admx_path}: policy '{name}' must write {REGISTRY_PATH}")
        for attribute in ("displayName", "explainText", "presentation"):
            if not policy.get(attribute):
                errors.add(f"{admx_path}: policy '{name}' has no {attribute}")
        if policy.find(f"{ADMX_NS}supportedOn") is None:
            errors.add(f"{admx_path}: policy '{name}' has no supportedOn")

        if name in LIST_KEYS:
            element = policy.find(f"{ADMX_NS}elements/{ADMX_NS}list")
            if element is None:
                errors.add(f"{admx_path}: policy '{name}' must use a list element")
            else:
                expected = f"{REGISTRY_PATH}\\{name}"
                if element.get("key") != expected:
                    errors.add(f"{admx_path}: list '{name}' must write the subkey {expected}")
                if element.get("additive") != "false":
                    errors.add(f"{admx_path}: list '{name}' must be additive=\"false\"")
                if element.get("valuePrefix") != "":
                    errors.add(
                        f"{admx_path}: list '{name}' must have valuePrefix=\"\" "
                        "(otherwise Group Policy names each value after its data, "
                        "not 1, 2, ...)"
                    )
        elif name == "DisableUpdateCheck":
            if policy.get("valueName") != name:
                errors.add(f"{admx_path}: policy '{name}' must set valueName=\"{name}\"")
            if policy.find(f"{ADMX_NS}enabledValue/{ADMX_NS}decimal") is None:
                errors.add(f"{admx_path}: policy '{name}' has no enabledValue decimal")
            if policy.find(f"{ADMX_NS}disabledValue/{ADMX_NS}decimal") is None:
                errors.add(f"{admx_path}: policy '{name}' has no disabledValue decimal")
        elif name == "ManagedProfiles":
            element = policy.find(f"{ADMX_NS}elements/{ADMX_NS}multiText")
            if element is None or element.get("valueName") != name:
                errors.add(f"{admx_path}: policy '{name}' must use a multiText element named {name}")
        elif name in ("ClientId", "ManagedProfilesUrl"):
            element = policy.find(f"{ADMX_NS}elements/{ADMX_NS}text")
            if element is None or element.get("valueName") != name:
                errors.add(f"{admx_path}: policy '{name}' must use a text element named {name}")

    # Presentations reference the elements they present.
    for presentation in adml.iter(f"{ADMX_NS}presentation"):
        for node in presentation:
            if node.tag.split("}")[-1] in ("textBox", "listBox", "multiTextBox") and not node.get("refId"):
                errors.add(f"{adml_path}: presentation '{presentation.get('id')}' has an element with no refId")


# --------------------------------------------------------------------------- plists


def mcx_settings(path: Path, errors: Errors) -> dict | None:
    """The forced settings of a com.apple.ManagedClient.preferences mobileconfig."""
    root = load_plist(path, errors)
    if root is None:
        return None
    if not isinstance(root, dict):
        errors.add(f"{path}: is not a property list dictionary")
        return None

    for payload in root.get("PayloadContent", []) or []:
        if not isinstance(payload, dict):
            continue
        payload_type = payload.get("PayloadType")
        if payload_type == PREFERENCE_DOMAIN:
            # The "custom settings" variant: the keys sit next to the payload keys.
            return {key: value for key, value in payload.items() if not key.startswith("Payload")}
        if payload_type != "com.apple.ManagedClient.preferences":
            continue
        domain = (payload.get("PayloadContent") or {}).get(PREFERENCE_DOMAIN)
        if not isinstance(domain, dict):
            errors.add(f"{path}: no '{PREFERENCE_DOMAIN}' dictionary in the managed preferences payload")
            return None
        forced = domain.get("Forced")
        if not isinstance(forced, list) or not forced:
            errors.add(f"{path}: '{PREFERENCE_DOMAIN}' has no Forced array")
            return None
        settings = forced[0].get("mcx_preference_settings") if isinstance(forced[0], dict) else None
        if not isinstance(settings, dict):
            errors.add(f"{path}: Forced item has no mcx_preference_settings dictionary")
            return None
        return settings

    errors.add(f"{path}: no com.apple.ManagedClient.preferences payload for {PREFERENCE_DOMAIN}")
    return None


# --------------------------------------------------------------------------- .reg files


def check_reg(path: Path, keys: list[str], errors: Errors,
              example_profiles: list | None = None) -> None:
    text = read_text(path, errors)
    if text is None:
        return
    if not text.startswith("Windows Registry Editor Version 5.00"):
        errors.add(f"{path}: does not start with the Windows Registry Editor header")

    policy_key = rf"HKEY_LOCAL_MACHINE\{REGISTRY_PATH.replace('Software', 'SOFTWARE')}"
    if f"[{policy_key}]" not in text:
        errors.add(f"{path}: does not write [{policy_key}]")

    for key in keys:
        if key in LIST_KEYS:
            subkey = f"[{policy_key}\\{key}]"
            if subkey not in text:
                errors.add(f"{path}: list key '{key}' has no {subkey} subkey")
            elif not re.search(rf"\[{re.escape(policy_key)}\\{key}\]\s*\n\"1\"=", text):
                errors.add(f"{path}: subkey for '{key}' has no \"1\" value")
        elif f'"{key}"=' not in text:
            errors.add(f"{path}: value '{key}' is missing")

    # The multi-string bytes have to decode to the profile set the example publishes.
    # `hex(7):` bytes are conventionally lower-case but the .reg format does not
    # require it, so match both cases.
    match = re.search(r'"ManagedProfiles"=hex\(7\):((?:[0-9a-fA-F]{2},?|\\\s*\n\s*)+)', text)
    if match:
        raw = re.sub(r"[\\\s]", "", match.group(1))
        try:
            data = bytes(int(byte, 16) for byte in raw.split(",") if byte)
            decoded = data.decode("utf-16-le").rstrip("\x00")
        except (ValueError, UnicodeDecodeError) as exc:
            errors.add(f"{path}: ManagedProfiles hex(7) bytes do not decode as UTF-16LE ({exc})")
        else:
            check_profiles_value(decoded, f"{path} (ManagedProfiles)", errors)
            if example_profiles is not None:
                example_profiles.append((path, decoded))
    elif '"ManagedProfiles"=' in text:
        match = re.search(r'"ManagedProfiles"="(.*)"', text)
        if match:
            value = match.group(1).replace('\\"', '"')
            check_profiles_value(value, f"{path} (ManagedProfiles)", errors)
            if example_profiles is not None:
                example_profiles.append((path, value))
        else:
            errors.add(
                f"{path}: ManagedProfiles is neither a hex(7) byte value nor a "
                "quoted REG_SZ string"
            )


# --------------------------------------------------------------------------- the kit


def validate(kit: Path, keys_path: Path, errors: Errors) -> int:
    keys = parse_keys(keys_path, errors)
    # Every ManagedProfiles value found under example/, to compare against example/profiles.json.
    example_profiles: list[tuple[Path, object]] = []

    check_urls(kit, errors)
    check_admx(kit / "windows" / "Elevate.admx", kit / "windows" / "en-US" / "Elevate.adml", keys, errors)

    # macOS templates and the worked example's mobileconfig.
    for path, placeholders in ((kit / "macos" / "no.reothor.elevate.mobileconfig", True),
                               (kit / "example" / "no.reothor.elevate.mobileconfig", False)):
        settings = mcx_settings(path, errors)
        if settings is not None:
            check_key_set(list(settings), keys, f"{path}", errors)
            check_values(settings, f"{path}", errors, placeholders_allowed=placeholders)
            if not placeholders and "ManagedProfiles" in settings:
                example_profiles.append((path, settings["ManagedProfiles"]))

    intune = load_plist(kit / "macos" / "intune-preference-file.plist", errors)
    if isinstance(intune, dict):
        where = f"{kit / 'macos' / 'intune-preference-file.plist'}"
        check_key_set(list(intune), keys, where, errors)
        check_values(intune, where, errors, placeholders_allowed=True)
    elif intune is not None:
        errors.add(f"{kit / 'macos' / 'intune-preference-file.plist'}: is not a property list dictionary")

    manifest_path = kit / "macos" / "jamf-manifest.json"
    manifest = load_json(manifest_path, errors)
    if isinstance(manifest, dict):
        for field in ("title", "description", "properties"):
            if field not in manifest:
                errors.add(f"{manifest_path}: '{field}' is missing")
        properties = manifest.get("properties")
        if isinstance(properties, dict):
            check_key_set(list(properties), keys, f"{manifest_path}", errors)
            for name, schema in properties.items():
                if not isinstance(schema, dict):
                    errors.add(f"{manifest_path}: property '{name}' is not an object")
                    continue
                for field in ("type", "title", "description"):
                    if field not in schema:
                        errors.add(f"{manifest_path}: property '{name}' has no '{field}'")
                if "default" in schema:
                    errors.add(f"{manifest_path}: property '{name}' has a default, which would force a value")
                if schema.get("type") == "array" and not isinstance(schema.get("items"), dict):
                    errors.add(f"{manifest_path}: property '{name}' is an array with no 'items' schema")
                if name == "AllowedSignInMethods":
                    enum = (schema.get("items") or {}).get("enum")
                    if sorted(enum or []) != sorted(SIGN_IN_METHODS):
                        errors.add(f"{manifest_path}: property '{name}' must enumerate {sorted(SIGN_IN_METHODS)}")
                if not schema.get("links"):
                    errors.add(f"{manifest_path}: property '{name}' has no links to the key reference")
        elif properties is not None:
            errors.add(f"{manifest_path}: 'properties' is not an object")
    elif manifest is not None:
        errors.add(f"{manifest_path}: is not a JSON object")

    for path, placeholders in ((kit / "cli" / "managed.json", True),
                               (kit / "example" / "managed.json", False)):
        values = load_json(path, errors)
        if isinstance(values, dict):
            check_key_set(list(values), keys, f"{path}", errors)
            check_values(values, f"{path}", errors, placeholders_allowed=placeholders)
            if not placeholders and "ManagedProfiles" in values:
                example_profiles.append((path, values["ManagedProfiles"]))
        elif values is not None:
            errors.add(f"{path}: is not a JSON object")

    for path in (kit / "example" / "example.reg", kit / "windows" / "example.reg"):
        check_reg(path, keys, errors, example_profiles if path.parent.name == "example" else None)

    profiles_path = kit / "example" / "profiles.json"
    document = load_json(profiles_path, errors)
    if document is not None:
        validate_profile_set(document, f"{profiles_path}", errors)
        # The example publishes one document; every copy of it in the example has to say the same.
        for path, value in example_profiles:
            parsed = json.loads(value) if isinstance(value, str) else value
            if parsed != document:
                errors.add(f"{path}: ManagedProfiles differs from {profiles_path}")

    example_reg = kit / "example" / "example.reg"
    windows_reg = kit / "windows" / "example.reg"
    if example_reg.is_file() and windows_reg.is_file():
        if example_reg.read_bytes() != windows_reg.read_bytes():
            errors.add(f"{windows_reg}: is not a copy of {example_reg}")

    readme = read_text(kit / "README.md", errors)
    if readme is not None and readme.count("{{VERSION}}") != 1:
        errors.add(f"{kit / 'README.md'}: must contain the {{{{VERSION}}}} placeholder exactly once")

    for path in (kit / "example" / "README.md",):
        if not path.is_file():
            errors.add(f"{path}: is missing")

    # Anything else in the kit that claims to be JSON has to parse.
    for path in sorted(kit.rglob("*.json")):
        load_json(path, errors)

    return len(keys)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repository root holding enterprise/ and docs/enterprise/keys.md")
    parser.add_argument("--kit", type=Path, default=None,
                        help="the kit directory (default: <root>/enterprise)")
    parser.add_argument("--keys", type=Path, default=None,
                        help="the key reference (default: <root>/docs/enterprise/keys.md)")
    args = parser.parse_args()

    kit = args.kit or args.root / "enterprise"
    keys_path = args.keys or args.root / "docs" / "enterprise" / "keys.md"

    errors = Errors()
    if not kit.is_dir():
        print(f"error: {kit}: no such directory", file=sys.stderr)
        return 1
    if not keys_path.is_file():
        print(f"error: {keys_path}: no such file", file=sys.stderr)
        return 1

    count = validate(kit, keys_path, errors)
    if errors:
        for message in errors.messages:
            print(f"error: {message}", file=sys.stderr)
        return 1
    print(f"enterprise kit: {count} keys, all templates consistent")
    return 0


if __name__ == "__main__":
    sys.exit(main())
