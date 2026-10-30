// Opens the mod's Files editor in the headed Chrome and reports whether it is usable.
// Pass as-is through browser_run_code_unsafe (filename: tools/publish/nexus/open-files.js).
async (page) => {
  const browser = await page.context().browser().browserType().connectOverCDP('http://127.0.0.1:9222');
  const ctx = browser.contexts()[0];
  const p = ctx.pages().find(x => x.url().includes('/edit/files')) || ctx.pages()[0];
  await p.setViewportSize({ width: 1400, height: 1000 });
  const out = {};
  for (let attempt = 0; attempt < 4; attempt++) {
    await p.goto('https://www.nexusmods.com/games/moonsweepers/mods/80/edit/files', { waitUntil: 'domcontentloaded', timeout: 60000 });
    // Cloudflare's "Just a moment" clears on its own in a real window.
    for (let i = 0; i < 40; i++) { if (!/just a moment/i.test(await p.title())) break; await p.waitForTimeout(1000); }
    await p.waitForTimeout(6000);
    const body = (await p.locator('body').innerText()).replace(/\s+/g, ' ');
    // The page's own API calls can still be challenged on the first load; a reload fixes it.
    if (/Failed to (load|fetch) mod/.test(body)) { out.retried = attempt + 1; continue; }
    out.loggedIn = /Open profile menu/.test(body) && !/Log in|Sign in/.test(body.slice(0, 400));
    out.state = body.match(/(Published|Draft|Hidden)/)?.[1];
    const i = body.indexOf('Added files');
    out.files = body.slice(i, body.indexOf('Version management', i));
    return out;
  }
  out.error = 'the Files editor kept failing to load its mod settings';
  return out;
}
