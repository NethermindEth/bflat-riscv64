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
├── alpine.md            riscv-alpine-build: lp64d Alpine without FP/compressed instructions
├── runtime.md           dotnet-riscv: the patched .NET runtime
├── architecture.md      bflat pipeline, end to end (incl. postprocessing)
├── modules.md           Each link-time module explained
├── build.md             Building bflat and using it
└── verification.md      How every change is regression-tested
```

## Editing

Each page begins with front matter that drives the layout, eyebrow
text, and previous/next page links. Sidebar navigation is generated
from the `nav` list in `_config.yml`.
