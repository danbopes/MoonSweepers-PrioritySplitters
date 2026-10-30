// Saves the filled update form and waits until the new version is listed. Placeholder: __VERSION__.
async (page) => {
  const browser = await page.context().browser().browserType().connectOverCDP('http://127.0.0.1:9222');
  const ctx = browser.contexts()[0];
  const p = ctx.pages().find(x => x.url().includes('/edit/files'));
  const out = { api: [] };
  const onResp = async (r) => {
    const u = r.url();
    if (/\/api\/flamework\//.test(u)) {
      let body = '';
      if (r.status() >= 400) { try { body = (await r.text()).replace(/\s+/g, ' ').slice(0, 200); } catch {} }
      out.api.push({ status: r.status(), method: r.request().method(), url: u.slice(0, 120), body });
    }
  };
  p.on('response', onResp);
  await p.getByRole('button', { name: 'Save file' }).first().click();
  out.status = 'pending';
  for (let i = 0; i < 90; i++) {
    await p.waitForTimeout(2000);
    const body = (await p.locator('body').innerText()).replace(/\s+/g, ' ');
    const added = body.slice(body.indexOf('Added files'), body.indexOf('Version management'));
    if (/Priority Splitters __VERSION__/.test(added)) { out.status = 'listed'; out.added = added; break; }
    const err = body.match(/.{0,80}(error|failed|too large|invalid|try again).{0,120}/i);
    if (err && !/Failed to fetch mod settings/.test(err[0])) { out.status = 'error'; out.err = err[0]; break; }
  }
  p.off('response', onResp);
  return out;
}
