// Linux cloud smoke test. Uses an isolated database and random temporary credentials.
const { chromium } = require('playwright');
const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'club-browser-'));
const password = crypto.randomBytes(24).toString('base64url');
const environment = {...process.env, Club__DataDirectory:directory, Logging__LogLevel__Default:'Warning'};
const baseArgs = ['scripts/dotnet.sh','run','--project','src/Club.Server','-c','Release','--no-build','--'];
const errors = [];
let server, browser;
const delay = ms => new Promise(resolve => setTimeout(resolve,ms));
function runCommand(args,input) {
  const result=spawnSync('bash',[...baseArgs,...args],{cwd:root,env:environment,input,encoding:'utf8',timeout:30000});
  if(result.status!==0) throw new Error(result.stderr || result.stdout || String(result.error));
  return result.stdout;
}
async function boot() {
  const log=fs.openSync(path.join(directory,'server.log'),'a');
  server=spawn('bash',baseArgs,{cwd:root,env:environment,stdio:['ignore',log,log],detached:true});
  fs.closeSync(log);
  for(let i=0;i<100;i++) {
    if(server.exitCode!==null)throw new Error(fs.readFileSync(path.join(directory,'server.log'),'utf8'));
    try{const response=await fetch('http://127.0.0.1:5080/health');if(response.ok)return;}catch{}
    await delay(100);
  }
  throw new Error('Server did not start.');
}
async function stop() {
  if(!server || server.exitCode!==null)return;
  process.kill(-server.pid,'SIGINT');
  for(let i=0;i<80&&server.exitCode===null;i++)await delay(100);
  if(server.exitCode===null)process.kill(-server.pid,'SIGKILL');
}
async function login(page,username) {
  await page.goto('http://127.0.0.1:5080');
  await page.locator('#auth input[name=username]').fill(username);
  await page.locator('#auth input[name=password]').fill(password);
  await page.locator('#auth button').click();
  await page.locator('#logout').waitFor();
}
async function waitFor(predicate, message) {
  for(let i=0;i<100;i++){if(await predicate())return;await delay(100);}
  throw new Error(message);
}
(async()=>{
  try {
    try {await fetch('http://127.0.0.1:5080/health');throw new Error('Port 5080 is already used; stop your development server before running this check.');}
    catch(e){if(!e.message.includes('fetch failed'))throw e;}
    await boot();
    let launch = {headless:true};
    if(process.env.CLUB_PACKAGED_CHROMIUM==='1') {
      const module=require('@sparticuz/chromium');
      const packaged=module.default || module;
      // Single-process mode shares cookies between contexts; use normal isolated browser contexts.
      launch={...launch,executablePath:await packaged.executablePath(),args:packaged.args.filter(arg=>arg!=='--single-process')};
    }
    browser=await chromium.launch(launch);
    const adminContext=await browser.newContext({viewport:{width:1440,height:1000}});
    const visitorContext=await browser.newContext({viewport:{width:1100,height:800}});
    const admin=await adminContext.newPage(), visitor=await visitorContext.newPage();
    for(const page of [admin,visitor])page.on('pageerror',e=>errors.push(e.message));
    admin.on('dialog',dialog=>dialog.accept('Проверка прототипа'));
    visitor.on('dialog',dialog=>dialog.accept());
    await admin.goto('http://127.0.0.1:5080');
    assert.equal(await admin.evaluate(()=>getComputedStyle(document.body).backgroundColor),'rgb(0, 0, 0)');
    await admin.locator('#theme-toggle').click();
    assert.equal(await admin.locator('#theme-toggle').textContent(),'Чёрный фон');
    await admin.reload();
    assert.equal(await admin.evaluate(()=>document.documentElement.dataset.theme),'light');
    await admin.locator('#theme-toggle').click();
    await admin.reload();
    assert.equal(await admin.evaluate(()=>getComputedStyle(document.body).backgroundColor),'rgb(0, 0, 0)');
    assert.equal(await admin.locator('#theme-toggle').getAttribute('aria-pressed'),'true');
    await admin.locator('#owner-setup input[name=username]').fill('owner');
    await admin.locator('#owner-setup input[name=password]').fill(password);
    await admin.locator('#owner-setup input[name=confirmPassword]').fill('different-password');
    await admin.locator('#owner-setup button').click();
    await waitFor(()=>admin.locator('#owner-setup button').isEnabled(),'Setup validation did not release form.');
    assert.equal(await admin.evaluate(()=>fetch('/api/security').then(r=>r.json()).then(x=>x.ownerExists)),false);
    await admin.locator('#owner-setup input[name=confirmPassword]').fill(password);
    await admin.locator('#owner-setup button').click();
    await admin.locator('#logout').waitFor();
    await admin.reload();
    await admin.locator('#logout').waitFor();
    await admin.locator('#computer input').fill('ПК-01');
    await admin.locator('#computer button').click();
    await admin.locator('.computer').waitFor();
    await admin.locator('[data-tab=settings]').click();
    await admin.locator('#package input[name=name]').fill('Десять минут');
    await admin.locator('#package input[name=minutes]').fill('10');
    await admin.locator('#package input[name=price]').fill('100');
    await admin.locator('#package button[type=reset]').blur();
    await admin.locator('#package button').first().click();
    await admin.locator('[data-edit]').waitFor();
    await visitor.goto('http://127.0.0.1:5080');
    await visitor.locator('#switch-auth').click();
    await visitor.locator('#auth input[name=username]').fill('visitor');
    await visitor.locator('#auth input[name=password]').fill(password);
    await visitor.locator('#auth button').click();
    await visitor.locator('#logout').waitFor();
    await admin.locator('[data-tab=money]').click();
    await admin.locator('#topup select[name=customerId] option').waitFor({state:'attached'});
    await admin.locator('#topup input[name=amount]').fill('200');
    // Server commits, but browser never receives the response: retry must not credit twice.
    await admin.route('**/api/admin/topups',async route=>{await route.fetch();await route.abort('failed');});
    await admin.locator('#topup button').click();
    await waitFor(()=>admin.locator('#topup button').isEnabled(),'Top-up failure did not release form.');
    await admin.unroute('**/api/admin/topups');
    await admin.locator('#retry-operation').click();
    await admin.locator('#pending-operation').waitFor({state:'hidden'});
    await waitFor(()=>visitor.locator('[data-buy]').isEnabled(),'Visitor balance did not update.');
    await visitor.locator('[data-buy]').click();
    await visitor.locator('.timer').waitFor();
    const visitorState=await visitor.evaluate(()=>fetch('/api/state').then(r=>r.json()));
    assert.equal(visitorState.user.balanceMinor,10000);
    await admin.locator('[data-tab=overview]').click();
    await admin.locator('[data-action=pause]').waitFor();
    await admin.locator('[data-action=pause]').click();
    await admin.locator('[data-action=resume]').waitFor();
    await admin.locator('[data-action=resume]').click();
    await admin.locator('[data-action=pause]').waitFor();
    await stop(); await boot();
    await admin.reload();
    await admin.locator('[data-action=resume]').waitFor();
    await admin.locator('[data-action=end]').click();
    await admin.locator('[data-refund]').waitFor();
    await admin.locator('[data-refund]').click();
    await admin.locator('#refund input[name=amount]').fill('10');
    await admin.locator('#refund input[name=reason]').fill('Учебная компенсация');
    await admin.locator('#refund button').first().click();
    await admin.locator('[data-tab=money]').click();
    const summary=await admin.evaluate(()=>fetch('/api/state').then(r=>r.json()));
    assert.equal(summary.report.topUpsMinor,20000);
    assert.equal(summary.report.purchasesMinor,10000);
    assert.equal(summary.report.refundsMinor,1000);
    assert.equal(summary.report.cashNetMinor,19000);
    assert.equal(summary.report.walletBalancesMinor,10000);
    const downloadEvent=admin.waitForEvent('download');await admin.locator('#backup').click();
    const download=await downloadEvent, backup=path.join(directory,'usb-copy.db');
    await download.saveAs(backup);runCommand(['--verify-backup',backup]);
    fs.mkdirSync(path.join(root,'artifacts'),{recursive:true});
    await admin.screenshot({path:path.join(root,'artifacts','admin-report.png'),fullPage:true});
    await admin.setViewportSize({width:390,height:844});
    assert.equal(await admin.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),true,'Mobile page must not overflow horizontally.');
    await admin.screenshot({path:path.join(root,'artifacts','admin-mobile.png'),fullPage:true});
    assert.deepEqual(errors,[]);
    console.log('PASS: black/light theme switch and reload persistence, first-run owner setup without CLI, password confirmation, reload, browser registration, login, package setup, lost-response retry, purchase, pause/resume, real server restart, manual refund, report and downloaded backup. No page errors.');
  } catch(error) {
    if(browser) {
      fs.mkdirSync(path.join(root,'artifacts'),{recursive:true});
      for(const [i,context] of browser.contexts().entries()) {
        for(const [j,page] of context.pages().entries()) {
          console.error(`Page ${i}/${j}:`,await page.locator('body').innerText().catch(()=>''));
          await page.screenshot({path:path.join(root,'artifacts',`browser-failure-${i}-${j}.png`),fullPage:true}).catch(()=>{});
        }
      }
      console.error('Browser page errors:',errors);
    }
    throw error;
  } finally {
    if(browser)await browser.close();await stop();fs.rmSync(directory,{recursive:true,force:true});
  }
})().catch(error=>{console.error(error);process.exitCode=1;});
