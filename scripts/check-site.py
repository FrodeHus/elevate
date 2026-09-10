#!/usr/bin/env python3
"""Validate the deployable site and its GitHub documentation links, without dependencies."""
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SITE = ROOT / "site"
errors = []


class Page(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.ids = set()
        self.links = []
        self.assets = []
        self.h1_count = 0

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "h1":
            self.h1_count += 1
        if "id" in attrs:
            if attrs["id"] in self.ids:
                errors.append(f'Duplicate id: {attrs["id"]}')
            self.ids.add(attrs["id"])
        if tag == "a":
            self.links.append(attrs.get("href", ""))
        if tag in ("img", "script"):
            self.assets.append(attrs.get("src", ""))
        if tag == "link" and attrs.get("rel") in ("stylesheet", "icon"):
            self.assets.append(attrs.get("href", ""))
        if tag == "img":
            for required in ("alt", "width", "height"):
                if required not in attrs:
                    errors.append(f'Image missing {required}: {attrs.get("src")}')


page = Page()
index = SITE / "index.html"
if not index.is_file():
    sys.exit("Missing site/index.html")
page.feed(index.read_text())
if page.h1_count != 1:
    errors.append("Page must have exactly one h1")

for asset in page.assets:
    parsed = urlsplit(asset)
    target = (SITE / unquote(parsed.path)).resolve()
    if parsed.scheme or asset.startswith("/") or not target.is_relative_to(SITE):
        errors.append(f"Asset must be self-contained and relative for project Pages: {asset}")
    elif not target.is_file():
        errors.append(f"Missing asset: {asset}")

for link in page.links:
    parsed = urlsplit(link)
    if not link:
        errors.append("Empty link")
    elif link.startswith("#"):
        if parsed.fragment and unquote(parsed.fragment) not in page.ids:
            errors.append(f"Missing section: {link}")
    elif parsed.netloc.lower() == "github.com":
        prefix = "/frodehus/elevate/blob/main/"
        if parsed.path.lower().startswith(prefix):
            path = unquote(parsed.path[len(prefix):])
            target = (ROOT / path).resolve()
            if not target.is_relative_to(ROOT) or not target.is_file():
                errors.append(f"Missing GitHub documentation target: {path}")
            elif parsed.fragment:
                headings = re.findall(r"^#{1,6}\s+(.+?)\s*#*\s*$", target.read_text(), re.M)
                slugs = {re.sub(r"[^\w\- ]", "", title.lower()).replace(" ", "-") for title in headings}
                if unquote(parsed.fragment) not in slugs:
                    errors.append(f"Missing documentation heading: {link}")
    elif parsed.scheme != "https":
        errors.append(f"Unexpected link destination: {link}")

# All CSS assets must also survive deployment under /elevate/.
css = (SITE / "styles.css").read_text()
for asset in re.findall(r"url\(['\"]?([^)'\"]+)", css):
    if asset.startswith("data:"):
        continue
    target = (SITE / asset).resolve()
    if not target.is_relative_to(SITE) or not target.is_file():
        errors.append(f"Missing or external CSS asset: {asset}")

for path in SITE.rglob("*"):
    if path.is_symlink():
        errors.append(f"Pages artifact cannot contain symlinks: {path}")

if errors:
    print("\n".join(f"ERROR: {error}" for error in errors))
    sys.exit(1)
print(f"Site valid: {len(page.links)} links, {len(page.assets)} asset references, {len(page.ids)} unique IDs.")
