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
        if tag in ("img", "script") and attrs.get("src"):
            self.assets.append(attrs["src"])
        if tag == "link" and attrs.get("rel") in ("stylesheet", "icon"):
            self.assets.append(attrs.get("href", ""))
        if tag == "img":
            for required in ("alt", "width", "height"):
                if required not in attrs:
                    errors.append(f'Image missing {required}: {attrs.get("src")}')


pages = {}
for document in sorted(SITE.glob("*.html")):
    page = Page()
    page.feed(document.read_text())
    pages[document.resolve()] = page
    if page.h1_count != 1:
        errors.append(f"{document.name}: must have exactly one h1")
if (SITE / "index.html").resolve() not in pages:
    sys.exit("Missing site/index.html")

for document, page in pages.items():
    for asset in page.assets:
        parsed = urlsplit(asset)
        target = (document.parent / unquote(parsed.path)).resolve()
        if parsed.scheme or asset.startswith("/") or not target.is_relative_to(SITE):
            errors.append(f"Asset must be self-contained and relative for project Pages: {asset}")
        elif not target.is_file():
            errors.append(f"Missing asset: {asset}")

    for link in page.links:
        parsed = urlsplit(link)
        if not link:
            errors.append(f"{document.name}: empty link")
        elif not parsed.scheme and not parsed.netloc:
            target = (document.parent / unquote(parsed.path)).resolve() if parsed.path else document
            if target.is_dir():
                target /= "index.html"
            if link.startswith("/") or target not in pages:
                errors.append(f"{document.name}: missing or non-relative page: {link}")
            elif parsed.fragment and unquote(parsed.fragment) not in pages[target].ids:
                errors.append(f"{document.name}: missing section: {link}")
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
print(f"Site valid: {len(pages)} pages, {sum(len(p.links) for p in pages.values())} links, "
      f"{sum(len(p.assets) for p in pages.values())} asset references.")
