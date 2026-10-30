// Reads the public mod page: the mod version and the state of the new file. Placeholder: __VERSION__.
// A fresh file reads "not yet available for download" while the virus scan runs; that clears on its own.
async (page) => {
  const b = await page.context().browser().browserType().connectOverCDP('http://127.0.0.1:9222');
  const ctx = b.contexts()[0];
  const p = ctx.pages().find(x => /mods\/80(\?|$)/.test(x.url())) || ctx.pages().find(x => x.url().includes('/edit/files'));
  await p.goto('https://www.nexusmods.com/moonsweepers/mods/80?tab=files', { waitUntil: 'domcontentloaded', timeout: 60000 });
  for (let i = 0; i < 40; i++) { if (!/just a moment/i.test(await p.title())) break; await p.waitForTimeout(1000); }
  await p.waitForTimeout(4000);
  const body = (await p.locator('body').innerText()).replace(/\s+/g, ' ');
  return {
    modVersion: body.match(/Version ([0-9.]+) Add media/)?.[1],
    newFile: body.match(/Priority Splitters __VERSION__[^|]{0,200}/)?.[0],
  };
}
