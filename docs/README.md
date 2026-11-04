# bflat-riscv64 documentation site

A Jekyll site in the style of [nethermind.io](https://www.nethermind.io).
It is in `docs/`, so GitHub Pages can serve it, and you can run it locally.

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
├── assets/css/style.scss  Theme: dark navy and Nethermind orange
├── index.html           Marketing landing page (uses landing layout)
├── runtime.md           dotnet-riscv: the patched .NET 11 build that bflat uses
├── architecture.md      The pipeline end to end, incl. ILC substitutions and postprocessing
├── modules.md           Each module explained
├── targets.md           The zkVM platforms and the other targets, one by one
├── manifest.md          The manifest format of the modules, platforms and packages
├── build.md             Building the driver (.NET version, variant) and using it
├── signing.md           The signatures of the downloaded packages and of the dotnet-riscv release
└── verification.md      Contracts, tests, fuzzing, proofs and the CI gates
```

## Editing

Each page starts with front matter. It sets the layout, the eyebrow
text and the previous and next page links. The sidebar navigation comes
from the `nav` list in `_config.yml`.

Pages link to each other as `page.md`. `jekyll-relative-links` changes
these links to the permalinks. GitHub Pages enables it by default. The
`Gemfile` and `_config.yml` list it too, so a local build resolves the
links in the same way. Without it, the links of a local preview give 404,
and the links of the published site work.
