# bflat-riscv64 documentation site

Jekyll site styled after [nethermind.io](https://www.nethermind.io). Lives
under `docs/` so it can be served from GitHub Pages or run locally.

## Local preview

```console
$ cd docs
$ bundle install
$ bundle exec jekyll serve
```

Open <http://localhost:4000> in a browser.

## Layout

```
docs/
├── _config.yml          Site config and sidebar nav
├── _layouts/
│   ├── default.html     Doc-page shell with sidebar
│   └── landing.html     Full-width landing layout (no sidebar)
├── _includes/           header, sidebar, footer + reusable SVG diagrams
├── assets/css/style.scss  Theme — dark navy + Nethermind orange
├── index.html           Marketing landing page (uses landing layout)
├── runtime.md           dotnet-riscv: building .NET for the target, and the aim of not patching it
├── architecture.md      The pipeline end to end, incl. ILC substitutions and postprocessing
├── modules.md           Each link-time module explained
├── build.md             Building the driver (.NET version, variant) and using it
└── verification.md      Contracts, tests, fuzzing, proofs and the CI gates
```

## Editing

Each page begins with front matter that drives the layout, eyebrow
text, and previous/next page links. Sidebar navigation is generated
from the `nav` list in `_config.yml`.

Pages cross-link as `page.md`; `jekyll-relative-links` rewrites those to
their permalinks. It is enabled by default on GitHub Pages and listed in
the `Gemfile` / `_config.yml` so a local build resolves them the same way —
without it, local previews produce links that 404 while production works.
