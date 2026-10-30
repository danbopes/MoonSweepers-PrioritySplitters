// Answers a bug report, sets its status and closes it. Placeholders: __BUG_TITLE_JSON__ (JSON string of the
// report title), __ISSUE_ID__ (number from the report's "Change status" link), __REPLY_JSON__ (JSON string),
// __STATUS__ (Fixed, Not a bug, Won't fix, Duplicate, Known issue, Needs more info, Being looked at).
// Set __CLOSE__ to true to close the report after replying, false to leave it open.
async (page) => {
  const browser = await page.context().browser().browserType().connectOverCDP('http://127.0.0.1:9222');
  const ctx = browser.contexts()[0];
  const b = ctx.pages().find(x => x.url().includes('tab=bugs')) || ctx.pages()[0];
  const out = {};
  b.on('dialog', d => { out.dialogs = (out.dialogs || []).concat(d.message().slice(0, 120)); d.accept(); });
  await b.goto('https://www.nexusmods.com/moonsweepers/mods/80?tab=bugs', { waitUntil: 'domcontentloaded', timeout: 60000 });
  for (let i = 0; i < 40; i++) { if (!/just a moment/i.test(await b.title())) break; await b.waitForTimeout(1000); }
  await b.waitForTimeout(3000);
  await b.locator('a', { hasText: __BUG_TITLE_JSON__ }).first().click();
  await b.waitForTimeout(4000);
  // The reply box is a wysibb BBCode editor: type into its contenteditable, the hidden textarea syncs on submit.
  const editor = b.locator('#add-bug-reply-post-__ISSUE_ID__').locator('xpath=ancestor::*[contains(@class,"wysibb")][1]//div[contains(@class,"wysibb-text-editor")]').first();
  for (let i = 0; i < 3 && !(await editor.isVisible().catch(() => false)); i++) {
    await b.locator('.show-bug-reply').first().click();
    await b.waitForTimeout(1500);
  }
  if (!(await editor.isVisible().catch(() => false))) { out.error = 'reply editor never became visible'; return out; }
  await editor.click();
  await editor.fill(__REPLY_JSON__);
  await b.locator('#submit-add-bug-reply-form-__ISSUE_ID__').click();
  await b.waitForTimeout(6000);
  let body = (await b.locator('body').innerText()).replace(/\s+/g, ' ');
  const probe = (__REPLY_JSON__).slice(0, 40);
  out.replyPosted = body.includes(probe);
  if (!out.replyPosted) { out.error = 'the reply did not appear'; return out; }
  // The status control is a select2 over a hidden <select>; the site listens through jQuery.
  out.status = await b.evaluate(() => {
    const sel = document.querySelector('select[id="changeissuestatus___ISSUE_ID__"]');
    const opt = sel && [...sel.options].find(o => o.text.trim() === '__STATUS__');
    if (!opt) return 'no option __STATUS__';
    sel.value = opt.value;
    window.jQuery ? window.jQuery(sel).trigger('change') : sel.dispatchEvent(new Event('change', { bubbles: true }));
    return opt.text.trim();
  });
  await b.waitForTimeout(4000);
  if (__CLOSE__) {
    // "Close issue" sits in the report's Manage menu; the link just calls changeIssueLocking(id).
    await b.locator('#manage-issue-__ISSUE_ID__').click();
    await b.waitForTimeout(1000);
    const lock = b.locator('#issue-lock-action___ISSUE_ID__');
    out.lockAction = (await lock.innerText().catch(() => '')).trim();
    if (/^Close issue$/.test(out.lockAction)) {
      if (await lock.isVisible().catch(() => false)) await lock.click();
      else await b.evaluate(() => changeIssueLocking(__ISSUE_ID__));
      await b.waitForTimeout(5000);
    }
  }
  await b.goto('https://www.nexusmods.com/moonsweepers/mods/80?tab=bugs', { waitUntil: 'domcontentloaded', timeout: 60000 });
  for (let i = 0; i < 40; i++) { if (!/just a moment/i.test(await b.title())) break; await b.waitForTimeout(1000); }
  await b.waitForTimeout(3000);
  body = (await b.locator('body').innerText()).replace(/\s+/g, ' ');
  const i = body.indexOf('Bug title');
  out.listAfter = body.slice(i, i + 300);
  return out;
}
