---
name: publish
description: Release a new Priority Splitters version to Nexus Mods (mod 80) and answer or close a bug report there. Use for /publish, "release 1.0.x", "upload to Nexus", "republish", "reply to the Nexus bug report".
---

# Publish a Priority Splitters release

Nexus Mods has no upload API and its Cloudflare check only clears in a real browser window, so the upload is
driven through a visible Chrome on the desktop that holds the Nexus login, attached to the Playwright MCP
over the DevTools port. Everything below is scripted under `tools/publish/`; this file is the order and the
gotchas. The whole run takes about ten minutes.

## 0. Before starting

- The fix is committed on `main` and the version is bumped in all four places: `<Version>` in
  `src/PrioritySplitters.csproj`, `Version` in `src/Plugin.cs`, the title line of `tools/nexus/README.txt`,
  and a new entry at the top of `tools/nexus/changelog.txt` (`package.sh` refuses to run otherwise).
- Decide the changelog line (one sentence, what was wrong and what changed) and, for a bug report, the
  reply text. The user has said replies to reporters on their own mod do not need approval.
- The game may be running: the Release build does not touch its plugins folder.

## 1. Build and package

```
tools/publish/package.sh
```

Builds Release, checks the DLL carries the version, writes `release/PrioritySplitters-<version>.zip`
(`BepInEx/plugins/PrioritySplitters/PrioritySplitters.dll` plus `README.txt`). `release/` is gitignored.

## 2. Commit and push

Own repo: commit the version bump, changelog and fix, push `main`. No approval needed.

## 3. Start the headed Chrome

```
tools/publish/headed-chrome.sh start
```

Picks the Playwright MCP profile under `~/.cache/ms-playwright*/` whose cookie jar holds the freshest
`nexusmods_session` cookie, lifts the display variables from a desktop process, and opens Chrome with
`--remote-debugging-port=9222`. If it warns that the session expired, the user signs in inside that window
(Turnstile needs a real click); never type or print the password.

## 4. Drive Nexus through the Playwright MCP

Each step is one `mcp__plugin_playwright_playwright__browser_run_code_unsafe` call. The scripts in
`tools/publish/nexus/` connect with `page.context().browser().browserType().connectOverCDP('http://127.0.0.1:9222')`
and work in `browser.contexts()[0]`. Scripts without placeholders go through the `filename` parameter;
the others are read, their `__PLACEHOLDERS__` substituted (`*_JSON` ones take a JSON string literal, quotes
included), and passed as `code`.

1. `open-files.js` (`filename`): opens the Files editor, rides out the Cloudflare page and the "Failed to
   fetch mod settings" reload, and returns `loggedIn`, the mod state and the current file rows.
2. `upload.js`: `__PREVIOUS__` (display name of the file being replaced, from step 1), `__VERSION__`,
   `__ZIP__` (absolute path), `__CHANGELOG_JSON__`. Clicks Update on the old row, feeds the zip to the file
   chooser, sets the display name `Priority Splitters <version>`, the version, Archive existing file, Update
   mod version, and the changelog. Returns the form values: check them before the next step.
3. `save-file.js`: `__VERSION__`. Clicks Save file and polls until the new row is listed. Expect
   `POST .../changelogs/add` and `POST .../mods/save` at 200.
4. `verify-public.js`: `__VERSION__`. The public page should show the new mod version; the file reads
   "not yet available for download" while the virus scan runs, which clears on its own within minutes.
5. Bug report, if any: `bug-reply.js` with `__BUG_TITLE_JSON__`, `__ISSUE_ID__` (the number in the
   report's "Change status" link, visible in the bugs tab's links), `__REPLY_JSON__`, `__STATUS__`
   (usually `Fixed`), `__CLOSE__` (`true` to close it). Returns `replyPosted`, the status set and the list
   row afterwards.

Reply shape that worked (adjust the cause):

> Thanks for the log, that pinpointed it. 1.0.0 switched itself off whenever ... Fixed in 1.0.1, which is
> up now: ... Replace the DLL and it should stay enabled.

## 5. Stop the browser

```
tools/publish/headed-chrome.sh stop
```

Chrome holds the profile lock while it runs, and the next Playwright MCP session that hashes to the same
profile would fail to start.

## Gotchas learned the hard way

- Run the Playwright calls one at a time. Two `browser_run_code_unsafe` calls in flight kill each other's
  CDP connection ("Target page, context or browser has been closed").
- Tabs opened with `ctx.newPage()` vanish when the call ends. Reuse the existing tabs, or open one with
  `headed-chrome.sh tab <url>` (a `PUT /json/new` on the DevTools port).
- `globalThis` does not survive between calls, and the sandbox has no `process`, `require`, `fetch` or
  `CSS`; reconnect over CDP in every script and select by `[id="..."]`, not `CSS.escape`.
- The first page load after a Cloudflare clearance can leave the page's XHRs challenged
  (`cf-mitigated: challenge`, "Failed to fetch mod settings (403)"). Reload; `open-files.js` does.
- The Files editor is Headless UI: checkboxes are `[role=checkbox]` spans, the category is a listbox, and
  `Add changelog` matches two buttons (use `.first()`). The old row's Update button opens the file chooser
  with the "update existing file" relation preset, which is what keeps Vortex update tracking intact.
- The bug reply box is a wysibb BBCode editor: fill the `.wysibb-text-editor` contenteditable, not the
  hidden textarea. The status dropdown is select2 over a hidden `<select>`; set its value and
  `jQuery(sel).trigger('change')`. "Close issue" lives in the report's Manage menu (`#manage-issue-<id>` opens
  it; the link `#issue-lock-action_<id>` calls `changeIssueLocking(<id>)` and reads "Re-open issue" once closed).
- Nexus mod id 80, game id 10774. Editor pages: `/games/moonsweepers/mods/80/edit/{general,media,files,requirements,permissions}`.
