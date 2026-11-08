# Updating these screenshots

The screenshots in `docs/wiki/images` are taken from a real Hearth web host by `scripts/docs-screenshots.mjs`, which
drives a headless Chromium over the DevTools protocol (Node 22+, no npm packages needed).

## 1. Run Hearth on an empty data folder

So your own chats and memories don't end up in the docs:

```bash
HEARTH_DATA_DIR=/tmp/hearth-docs-data dotnet run --project src/Hearth.Web --urls http://localhost:5293 -- --Hearth:AllowLocalProcesses=true
```

Go through the Welcome wizard (connect a model, add the starter team) and create a couple of sample chats, e.g. one
plain question and one group chat that `@mentions` Writer and Critic.

## 2. Capture

```bash
node scripts/docs-screenshots.mjs http://localhost:5293 docs/wiki/images
```

This captures every page in the script's list at 1440×900 (2× pixel density), in dark mode. Set `SCHEME=light` for
light mode, and `CHROME=/path/to/chrome` if it can't find Chromium (it looks in Playwright's cache, then Google Chrome).

Capture only some shots by naming them:

```bash
node scripts/docs-screenshots.mjs http://localhost:5293 docs/wiki/images bots tools
```

One-off shots use `name=/path#Button|Other button`, which clicks buttons (by their starting text) before the shot.
`select:VALUE` picks a dropdown option and `wait:MS` pauses:

```bash
node scripts/docs-screenshots.mjs http://localhost:5293 docs/wiki/images \
  "welcome-bots=/welcome#select:ClaudeCode|Test and use|wait:6000" \
  "chat-group=/c/<conversation-id>"
```

Shots used by the wiki that need such arguments: `welcome-bots`, `welcome-phone`, `chat-solo`, `chat-group`,
`bot-editor*` (pointed at the Researcher bot with a routine and check added), `settings-accounts`,
`connections-microsoft`.

## Wiki layout

- `Home.md` — the start page; `_Sidebar.md` — navigation (GitHub wikis show it on every page).
- User guide pages, then `Admin-*.md` pages.
- Links are relative (`Bots.md`), so they work in the repository, on the docs site, and in a GitHub wiki.

## The docs site

`docs/wiki` is also published with GitHub Pages at <https://finepointcgi.github.io/hearth/>.
`.github/workflows/pages.yml` builds it with Jekyll on pull requests that touch the docs (to catch broken pages) and
publishes it on every push to `main`. You can also run it by hand from the **Actions** tab.

| File | What it does |
|---|---|
| `_config.yml` | Site title, and the plugins that turn `Bots.md` links into `Bots.html` and use each page's first heading as its title. `Home.md` is the front page. |
| `_layouts/default.html` | The page frame: top bar, the sidebar (read from `_Sidebar.md`), and an *Edit this page on GitHub* link. |
| `assets/wiki.css` | Styles, light and dark. |

A new page needs nothing extra: add the `.md` file and a line in `_Sidebar.md`.

To preview the site before pushing, build it with the same image GitHub uses (Docker), then serve `_site`:

```bash
docker run --rm -v "$PWD":/w -w /w -e INPUT_SOURCE=docs/wiki -e INPUT_DESTINATION=_site \
  -e GITHUB_WORKSPACE=/w -e GITHUB_REPOSITORY=finepointcgi/hearth -e INPUT_TOKEN="$(gh auth token)" \
  ghcr.io/actions/jekyll-build-pages:v1.0.13
python3 -m http.server 8000 --directory _site
```

Links in a local build start with the site's path on GitHub (`/hearth/`), so open pages from there or click through
from `_site/index.html`. Delete `_site` afterwards; it isn't committed.

To publish as a GitHub wiki instead: clone `https://github.com/<owner>/<repo>.wiki.git`, copy the contents of
`docs/wiki` (without `_config.yml`, `_layouts` and `assets`) into it, commit and push.
