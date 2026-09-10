# Elevate product page implementation plan

**Goal:** Build a polished, accessible static product page with the content of Elevate.pdf, existing Lift artwork, user-documentation links, and GitHub Pages publishing.

**Architecture:** Self-contained `site/` directory with semantic HTML, CSS and progressive JavaScript. No framework, remote fonts, analytics, or build dependencies. GitHub Actions validates local assets and documentation targets before uploading only the site directory; deployment runs only from main.

**Design:** White (#ffffff), mist (#f5f7fb), ink (#101d32), secondary (#526176), blue (#075bd8), midnight (#0e2138). System sans-serif typography echoes the native applications. A centered editorial hero introduces a product stage with Lift and the macOS panel. Spacious feature stories alternate with a dark expiry section, a transparent permissions explanation, enterprise deployment and platform downloads. Glass is reserved for navigation and the product stage. Motion is brief and respects reduced-motion settings.

- [x] Build the responsive page, using the repository's documentation screenshots and supplied mascot assets. Preserve all eleven PDF topics, including permission scope counts, consent limitations, write-scope risks, access-package questions opening in My Access and fleet diagnostics.
- [x] Add progressive feature navigation, clipboard feedback and keyboard interaction. Keep content and links usable with JavaScript disabled.
- [x] Add a static-site validator, GitHub Pages workflow, deployment instructions and repository links. Verify assets, fragment targets and GitHub documentation paths against the checkout.
- [x] Preview desktop and mobile layouts; check keyboard operation, reduced motion, no-JavaScript behavior, asset requests and JavaScript errors. Review the final diff and document any deployment steps that require repository settings.


## Verification

- `python3 scripts/check-site.py`: 42 links, 14 asset references and 24 unique IDs validated.
- `node --check site/script.js`: passed. Workflow YAML parsed successfully.
- Chromium: 320, 390, 768, 1024 and 1440px widths; no horizontal overflow or browser/asset errors.
- All four feature tabs; arrow wrapping, Home/End; permissions disclosure; clipboard success and
  rejection; reduced motion; JavaScript-disabled content and installation links passed.
- Visually reviewed the product showcase, each feature panel and enterprise layout on mobile,
  plus the desktop page sections. Original Lift artwork is retained.
- Deployment was not run remotely. Configure Pages to use GitHub Actions and merge to main.
