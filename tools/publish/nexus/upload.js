// Fills the "update existing file" form for a new version without saving it, so the values can be checked.
// Placeholders (substitute before passing as `code`): __PREVIOUS__ = display name of the file being replaced,
// e.g. Priority Splitters 1.0.0; __VERSION__ = 1.0.1; __ZIP__ = absolute path of the zip;
// __CHANGELOG_JSON__ = the changelog text as a JSON string literal (quotes included).
async (page) => {
  const browser = await page.context().browser().browserType().connectOverCDP('http://127.0.0.1:9222');
  const ctx = browser.contexts()[0];
  const p = ctx.pages().find(x => x.url().includes('/edit/files'));
  const out = {};
  await p.keyboard.press('Escape');
  const row = p.locator('tr', { hasText: '__PREVIOUS__' }).first();
  // "Update" on the old file's row opens the OS file picker with the "update existing file" relation preset.
  const chooserP = p.waitForEvent('filechooser', { timeout: 10000 });
  await row.getByRole('button', { name: 'Update' }).click();
  const chooser = await chooserP;
  await chooser.setFiles('__ZIP__');
  await p.waitForTimeout(5000);
  await p.locator('#display-name').fill('Priority Splitters __VERSION__');
  await p.locator('#file-version').fill('__VERSION__');
  // Headless UI checkboxes: archive the old file so it drops to "Previous files", and sync the mod version.
  const checks = await p.evaluate(() => [...document.querySelectorAll('[role=checkbox]')].map(e => ({ id: e.id, checked: e.getAttribute('aria-checked'), label: (document.querySelector(`label[for="${e.id}"]`)?.innerText || e.parentElement?.innerText || '').trim().slice(0, 60) })));
  for (const c of checks) {
    if (/Archive existing file|Update mod version/.test(c.label) && c.checked !== 'true') {
      await p.locator(`[id="${c.id}"]`).click();
      await p.waitForTimeout(400);
    }
  }
  if (await p.locator('textarea').count() === 0) {
    // Two buttons carry this name (the row and its toggle); either works.
    await p.getByRole('button', { name: 'Add changelog' }).first().click();
    await p.waitForTimeout(1000);
  }
  await p.locator('textarea').first().fill(__CHANGELOG_JSON__);
  await p.waitForTimeout(500);
  out.values = await p.evaluate(() => ({
    existing: document.querySelector('[id^=headlessui-combobox-input]')?.value,
    displayName: document.querySelector('#display-name')?.value,
    version: document.querySelector('#file-version')?.value,
    category: document.querySelector('[id^=headlessui-listbox-button]')?.innerText?.trim(),
    checks: [...document.querySelectorAll('[role=checkbox]')].map(e => (document.querySelector(`label[for="${e.id}"]`)?.innerText || e.parentElement?.innerText || '').trim().slice(0, 40) + '=' + e.getAttribute('aria-checked')),
    changelog: document.querySelector('textarea')?.value,
  }));
  return out;
}
