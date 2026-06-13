# Casual Preloader (headless engine)

Vendored copy of [cueki/casual-pre-loader](https://github.com/cueki/casual-pre-loader)
stripped down to what Eternal Launcher needs: the PyQt6 GUI, auto-updater,
mod downloader, docs site, build scripts, and upstream tests have been removed.

What remains:

- `core/` — the engine (services, handlers, operations, quickprecache) plus
  `core/api.py`, the headless `PreloaderEngine` facade
- `cli/` — the headless CLI (`python -m cli`), the official integration surface
- `backup/` — bundled vanilla TF2 files required for staging
- `data/` — particle system map and mod URL data

Integration contract: see `docs/preloader-integration-contract.md` in the repo root.

Install dependencies with:

```
pip install -r requirements.txt
```

Original project license: see `LICENSE`. For the standalone GUI application,
use the upstream project.
