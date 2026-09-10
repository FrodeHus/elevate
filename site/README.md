# Elevate product page

A self-contained static site for GitHub Pages: HTML, CSS and progressive JavaScript. No build,
package manager, external fonts, analytics or runtime service is required. This site is independent
of the macOS, Windows and CLI builds.

## Preview and validate

From the repository root:

```sh
python3 -m http.server 4173 --directory site
```

Open <http://localhost:4173>. Validate the page before publishing:

```sh
python3 scripts/check-site.py
node --check site/script.js
node --check site/consent.mjs
node --test scripts/consent.test.mjs
```

The validator checks asset paths, image attributes, duplicate IDs, navigation fragments, and
GitHub documentation files and heading targets against this checkout. It does not make network
requests or guarantee that external services are available.

For visual checks, use a desktop viewport and 390px and 320px mobile viewports. Check all four
feature tabs (including arrow keys, Home and End), clipboard feedback, the permissions disclosure,
and section links. With JavaScript disabled all four feature stories remain visible. With reduced
motion enabled the entrance animation and smooth scrolling are disabled. Download and setup links
always work without JavaScript. The countdown is an illustration, not a live role or timer.

## Publish

1. In the repository, open **Settings → Pages → Build and deployment** and select **GitHub Actions**.
2. Merge the page and `.github/workflows/pages.yml` to `main`.
3. The **Product page** workflow validates, packages and deploys to
   <https://elevate.reothor.no/>. It also supports **Run workflow** on `main`.
4. If the `github-pages` environment has protection rules, allow deployments from `main` and
   approve the deployment when GitHub requests it.

Pull requests validate without deployment permissions. Pushes that change the site, its validator
or workflow publish automatically from `main`; manual runs on other branches cannot deploy.
Only the HTML pages, JavaScript modules, `styles.css`, `script.js` and `assets/` enter the published artifact. GitHub
creates the `github-pages` environment if it does not already exist. No custom token is needed.
See [GitHub's custom Pages workflow guide](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages).

### Custom domain

The primary address is `https://elevate.reothor.no/`. The original
`https://frodehus.github.io/elevate/` address redirects to it.
GitHub Pages settings hold the custom domain; this Actions-based deployment does not need a
`CNAME` file. In the Azure DNS zone `reothor.no` (resource group `common`), the `elevate` CNAME
points to `frodehus.github.io`, without the repository path. Keep HTTPS enforcement enabled
once GitHub has issued the domain certificate.

## Content and assets

- The page covers the eleven topics in the supplied `Elevate.pdf`: just-in-time habits, eligible
  roles, bulk activation, profiles, access packages, expiry controls, policy, permissions and
  their risks, enterprise rollout, and platform availability.
- Feature previews come from `docs/images/tutorials/` and `docs/images/access-packages/` and are
  labelled as documentation previews with sample data.
- Lift artwork is copied from `docs/images/lift/`: `Lift-03-roles.png`, `Lift-07-expiry.png`, and
  `Lift-11-corporate.png`. CSS blends the original background into the page; source files are
  unchanged. App icon and social image come from `docs/images/`.
- Documentation and release links point to `FrodeHus/elevate` on GitHub. Version-specific asset
  URLs are deliberately avoided so downloads keep pointing to the latest release.
- Keep installation requirements and permission copy in sync with the linked user guides.
- If the deployment URL changes, update the canonical and Open Graph URLs in `index.html`.

Design references: [Apple materials](https://developer.apple.com/design/human-interface-guidelines/materials)
and the [WCAG 2.2 quick reference](https://www.w3.org/WAI/WCAG22/quickref/). The page uses glass
selectively, visible keyboard focus, semantic landmarks, responsive layouts and user-controlled
feature switching. It does not auto-rotate panels or gate content behind animation.

## Consent result

`consent.html` uses `consent.mjs` to display the Microsoft admin-consent callback locally.
It does not authenticate users or independently verify consent. Errors take precedence over
the consent flag. Success, declined, error, incomplete and direct visits have distinct guidance.
Technical details are collapsed initially; only scopes actually returned are listed. Check the
tenant copy button, mobile wrapping, reduced motion and JavaScript-disabled guidance when editing.

## Legal pages

`terms.html` and `privacy.html` are standalone pages linked from the main footer and from each
other. They share the stylesheet and do not load JavaScript. The validator checks all HTML pages
and their cross-page links before deployment. Update each page's revision date when its substance
changes. The privacy contact route is GitHub; never ask users to put private request details in
public issues. The MIT license remains the governing software license.
