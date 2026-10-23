'use strict';
const root = document.querySelector('#app');
const identity = document.querySelector('#identity');
let token, state, tab = 'overview', lastFetch = 0, busy = false, online = true, refundOpen = false;
const warned = new Set();
const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
const money = value => new Intl.NumberFormat('ru-KG', {style:'currency',currency:'KGS'}).format(value / 100);
const date = value => new Date(value).toLocaleString('ru-RU', {timeZone:'Asia/Bishkek'});
const statuses = {Running:'Идёт',Paused:'Приостановлен',Ended:'Завершён вручную',Completed:'Время истекло'};
const methods = {Cash:'Наличные',Transfer:'Перевод'};
const actions = {AccountCreated:'Создан аккаунт',ComputerCreated:'Добавлено место',PackageSaved:'Изменён пакет',TopUp:'Пополнение',SessionStarted:'Начат сеанс',FreeTimeGranted:'Бесплатное время','Session:pause':'Приостановка','Session:resume':'Возобновление','Session:end':'Завершение',ManualRefund:'Ручной возврат',RecoveryPause:'Восстановление после перезапуска',ClockGapPause:'Остановка после сбоя таймера',TimeExpired:'Время истекло',BackupRequested:'Резервная копия'};
function toast(text, error = false) {
  const el = document.querySelector('#message');
  el.textContent = text; el.className = 'toast' + (error ? ' error' : ''); el.hidden = false;
  clearTimeout(toast.timeout); toast.timeout = setTimeout(() => { el.hidden = true; }, error ? 10000 : 6000);
}
async function api(path, body) {
  const tracked = !!body?.operationId && path !== '/admin/staff';
  if (tracked) {
    const previous = JSON.parse(sessionStorage.getItem('club.pending') || 'null');
    if (previous && (previous.path !== path || previous.body.operationId !== body.operationId || previous.actorId !== state?.user.id))
      throw new Error('Сначала проверьте результат неподтверждённой операции в сообщении над страницей.');
    sessionStorage.setItem('club.pending', JSON.stringify({path,body,actorId:state?.user.id,actorName:state?.user.username}));
    showPending();
  }
  try {
    const response = await fetch('/api' + path, {method:body === undefined ? 'GET' : 'POST', headers:body === undefined ? {} : {'Content-Type':'application/json','X-CSRF-TOKEN':token}, body:body === undefined ? undefined : JSON.stringify(body)});
    if (!response.ok) {
      if (tracked && response.status >= 400 && response.status < 500) sessionStorage.removeItem('club.pending');
      const data = await response.json().catch(() => ({}));
      const error = new Error(data.error || (response.status === 429 ? 'Слишком много попыток. Подождите минуту.' : `Запрос не выполнен (${response.status}).`));
      error.status = response.status; throw error;
    }
    const text = await response.text();
    const result = text ? JSON.parse(text) : null;
    if (tracked) sessionStorage.removeItem('club.pending');
    return result;
  } finally { if (tracked) showPending(); }
}
function showPending() {
  const pending = JSON.parse(sessionStorage.getItem('club.pending') || 'null');
  const el = document.querySelector('#pending-operation'); el.hidden = !pending;
  if (!pending) {el.innerHTML='';return;}
  el.innerHTML=`Результат операции «${esc(pending.actorName)}» ещё не подтверждён. Не принимайте и не выдавайте деньги повторно. <button id="retry-operation" ${!state || state.user.id!==pending.actorId?'disabled':''}>Проверить результат</button>`;
  document.querySelector('#retry-operation').onclick=async()=>{
    if(busy)return;busy=true;
    try {await api(pending.path,pending.body);toast('Операция подтверждена. Повторного списания или пополнения нет.');}
    catch(e){toast(e.message,true);}
    finally{busy=false;refundOpen=false;await refresh(true);}
  };
}
async function security() { const data = await api('/security'); token = data.token; return data; }
function options(items, label) { return items.map(x => `<option value="${esc(x.id)}">${esc(label(x))}</option>`).join(''); }
function methodField() { return '<label>Способ оплаты<select name="method"><option value="Cash">Наличные</option><option value="Transfer">Перевод</option></select></label>'; }
function minor(value) {
  if (!/^\d+(\.\d{1,2})?$/.test(String(value))) throw new Error('Введите сумму с точностью до двух знаков после точки.');
  const [whole, fraction = ''] = String(value).split('.');
  const result = Number(whole) * 100 + Number(fraction.padEnd(2,'0'));
  if (!Number.isSafeInteger(result)) throw new Error('Слишком большая сумма.');
  return result;
}
async function start() {
  try {
    const setup = await security();
    if (!setup.ownerExists) {
      renderOwnerSetup();
      return;
    }
    await refresh(true);
  } catch (error) { root.innerHTML = `<p class="warning">${esc(error.message)} Обновите страницу после запуска сервера.</p>`; }
}
function renderOwnerSetup() {
  state=null;identity.innerHTML='';showPending();
  root.innerHTML='<section class="auth panel"><h1>Создание владельца</h1><p class="muted">Первый запуск. Создайте свой аккаунт для управления клубом. В дальнейшем сотрудники получают отдельные аккаунты.</p><form id="owner-setup"><label>Логин<input name="username" autocomplete="username" required minlength="3" maxlength="32" pattern="[a-zA-Z0-9_.\\-]+"></label><label>Пароль<input name="password" type="password" autocomplete="new-password" required minlength="10" maxlength="128"></label><label>Повторите пароль<input name="confirmPassword" type="password" autocomplete="new-password" required minlength="10" maxlength="128"></label><button>Создать владельца и войти</button></form><p class="muted">Логин: 3–32 латинские буквы, цифры, точка, дефис или подчёркивание. Пароль: от 10 символов.</p></section>';
  document.querySelector('#owner-setup').onsubmit=async event=>{
    event.preventDefault();const form=event.target,button=form.querySelector('button');button.disabled=true;
    const payload=Object.fromEntries(new FormData(form));
    try {
      if(payload.password!==payload.confirmPassword)throw new Error('Пароли не совпадают.');
      await api('/setup/owner',payload);
      await api('/login',{username:payload.username,password:payload.password});
      await security();tab='overview';await refresh(true);
    } catch(error) {
      // An interrupted response may have followed a successful creation: never offer to overwrite it.
      const setup=await security().catch(()=>null);
      if(setup?.ownerExists){renderAuth();toast('Аккаунт владельца уже создан. Войдите с выбранным логином и паролем.');}
      else {toast(error.message,true);button.disabled=false;}
    }
  };
}
function renderAuth(register = false) {
  state = null; identity.innerHTML = '';showPending();
  root.innerHTML = `<section class="auth panel"><h1>${register ? 'Аккаунт посетителя' : 'Добро пожаловать'}</h1><p class="muted">${register ? 'Баланс пополняет администратор клуба.' : 'Войдите, чтобы управлять клубом или выбрать пакет.'}</p><form id="auth"><label>Логин<input name="username" autocomplete="username" required minlength="3" maxlength="32" pattern="[a-zA-Z0-9_.\\-]+"></label><label>Пароль<input name="password" type="password" autocomplete="${register ? 'new-password' : 'current-password'}" required minlength="10" maxlength="128"></label><button>${register ? 'Создать аккаунт' : 'Войти'}</button></form><hr><button class="secondary" id="switch-auth">${register ? 'Уже есть аккаунт' : 'Зарегистрироваться'}</button></section>`;
  document.querySelector('#switch-auth').onclick = () => renderAuth(!register);
  document.querySelector('#auth').onsubmit = async event => {
    event.preventDefault(); const form = event.target; const button = form.querySelector('button'); button.disabled = true;
    try {
      const payload = Object.fromEntries(new FormData(form));
      if (register) await api('/register', payload);
      await api('/login', payload); await security(); tab = 'overview'; await refresh(true);
    } catch (error) { toast(error.message, true); button.disabled = false; }
  };
}
async function refresh(force = false) {
  if (busy) return;
  try {
    const next = await api('/state'); online = true;
    state = next; lastFetch = performance.now();
    // Keep a form's focus and values intact while the background poll updates timers.
    const editing = root.contains(document.activeElement) && ['INPUT','SELECT','TEXTAREA'].includes(document.activeElement.tagName);
    if (force || (!editing && !refundOpen)) render();
    updateTimers();
  } catch (error) {
    if (error.status === 401) { await security(); renderAuth(); }
    else { online = false; if (force) toast(error.message, true); }
  }
}
function render() {
  const user = state.user;
  showPending();
  identity.innerHTML = `<span>${esc(user.username)} · ${user.role === 'Customer' ? 'Посетитель' : user.role === 'Owner' ? 'Владелец' : 'Администратор'}</span><button class="secondary" id="logout">Выйти</button>`;
  document.querySelector('#logout').onclick = async () => { try { await api('/logout', {}); await security(); renderAuth(); } catch (e) { toast(e.message,true); } };
  if (user.role === 'Customer') { renderCustomer(); return; }
  root.innerHTML = `<div class="intro"><div><h1>Рабочее место администратора</h1><p class="muted">${state.computers.length} мест · ${state.sessions.filter(x => ['Running','Paused'].includes(x.status)).length} активных сеансов</p></div><button id="backup" class="secondary">Скачать резервную копию</button></div><div id="offline" hidden>Нет связи с сервером. Данные могут быть устаревшими.</div><nav class="tabs">${[['overview','Зал и сеансы'],['money','Баланс и отчёты'],['settings','Пакеты и сотрудники'],['audit','Журнал']].map(([key,label]) => `<button class="secondary" data-tab="${key}" aria-selected="${key === tab}">${label}</button>`).join('')}</nav><div id="content"></div>`;
  root.querySelectorAll('[data-tab]').forEach(button => button.onclick = () => { refundOpen=false; tab = button.dataset.tab; render(); });
  document.querySelector('#backup').onclick = downloadBackup;
  const content = document.querySelector('#content');
  if (tab === 'overview') renderOverview(content);
  if (tab === 'money') renderMoney(content);
  if (tab === 'settings') renderSettings(content);
  if (tab === 'audit') content.innerHTML = `<div class="panel"><h2>Последние 100 событий</h2><p class="muted">Время — Бишкек. Полный журнал сохраняется в базе и резервной копии.</p><div class="table-wrap"><table><thead><tr><th>Время</th><th>Сотрудник / посетитель</th><th>Действие</th><th>Подробности</th></tr></thead><tbody>${state.audit.map(x => `<tr><td>${date(x.createdMs)}</td><td>${esc(x.actor)}</td><td>${esc(actions[x.action] || x.action)}</td><td class="audit-details">${esc(x.details)}</td></tr>`).join('')}</tbody></table></div></div>`;
}
function sessionTable() {
  if (!state.sessions.length) return '<p class="empty">Сеансов пока нет. Зарегистрируйте посетителя, пополните его баланс и купите пакет из его аккаунта.</p>';
  return `<div class="table-wrap"><table><thead><tr><th>ПК / посетитель</th><th>Пакет</th><th>Состояние</th><th>Осталось</th><th>Действия</th></tr></thead><tbody>${state.sessions.map(s => `<tr><td><strong>${esc(s.computerName)}</strong><br>${esc(s.username)}</td><td>${esc(s.packageName)}<br><small>${money(s.priceMinor)}</small></td><td><span class="badge ${s.status === 'Paused' ? 'paused' : ['Ended','Completed'].includes(s.status) ? 'ended' : ''}">${statuses[s.status]}</span></td><td data-timer="${s.id}"></td><td class="actions">${s.status === 'Running' ? `<button class="secondary" data-session="${s.id}" data-action="pause">Пауза</button>` : ''}${s.status === 'Paused' ? `<button data-session="${s.id}" data-action="resume">Продолжить</button>` : ''}${['Running','Paused'].includes(s.status) ? `<button class="secondary" data-session="${s.id}" data-action="end">Завершить</button>` : ''}${s.availableRefundMinor > 0 ? `<button class="secondary" data-refund="${s.id}">Возврат до ${money(s.availableRefundMinor)}</button>` : ''}</td></tr>`).join('')}</tbody></table></div>`;
}
function renderOverview(content) {
  content.innerHTML = `<div class="grid">${state.computers.map(c => `<div class="computer ${c.occupied ? 'occupied' : ''}"><h3><span class="dot"></span>${esc(c.name)}</h3><p class="muted">${c.occupied ? 'Есть активный сеанс' : 'Свободно по учёту'}</p><small>Windows-клиент не подключён</small></div>`).join('')}</div><section class="panel"><h2>Сеансы</h2>${sessionTable()}</section><div class="grid"><section class="panel"><h2>Добавить тестовое место</h2><form id="computer"><label>Название<input name="name" placeholder="ПК-01" required maxlength="80"></label><button>Добавить</button></form></section><section class="panel"><h2>Бесплатное время</h2><form id="free"><label>Посетитель<select name="customerId" required>${options(state.customers,x=>x.username)}</select></label><label>Место<select name="computerId" required>${options(state.computers.filter(x=>!x.occupied),x=>x.name)}</select></label><label>Минуты<input type="number" name="minutes" min="1" max="1440" required></label><label>Причина<input name="reason" required maxlength="500"></label><button>Выдать время</button></form></section></div>`;
  bindForm('computer','/admin/computers',data=>data);
  bindForm('free','/admin/free-sessions',data=>({...data,minutes:Number(data.minutes)}));
  content.querySelectorAll('[data-session]').forEach(button => button.onclick = () => sessionAction(button));
  content.querySelectorAll('[data-refund]').forEach(button => button.onclick = () => showRefund(button.dataset.refund));
}
function renderMoney(content) {
  const report = state.report;
  content.innerHTML = `<div class="grid">${[['Поступило пополнений',report.topUpsMinor],['Оплачено пакетов',report.purchasesMinor],['Выдано возвратов',report.refundsMinor],['Остатки на балансах',report.walletBalancesMinor],['Наличные: приход − возвраты',report.cashNetMinor],['Переводы: приход − возвраты',report.transferNetMinor]].map(([label,value])=>`<div class="metric"><span>${label}</span><strong>${money(value)}</strong></div>`).join('')}</div><p class="muted">За всё время. Показатели не учитывают расходы клуба и начальный остаток кассы; это не расчёт прибыли.</p><div class="grid"><section class="panel"><h2>Пополнение баланса</h2><form id="topup"><label>Посетитель<select name="customerId" required>${options(state.customers,x=>`${x.username} · ${money(x.balanceMinor)}`)}</select></label><label>Сумма, сом<input name="amount" type="number" min="0.01" step="0.01" max="1000000" required></label>${methodField()}<p class="muted">Подтверждайте только после фактического получения оплаты.</p><button>Зарегистрировать пополнение</button></form></section><section class="panel"><h2>По сотрудникам</h2><div class="table-wrap"><table><thead><tr><th>Сотрудник</th><th>Операция</th><th>Способ</th><th>Сумма</th></tr></thead><tbody>${report.byEmployee.map(x=>`<tr><td>${esc(x.username)}</td><td>${x.kind==='TopUp'?'Пополнение':'Возврат'} (${x.operations})</td><td>${methods[x.method]}</td><td>${money(x.amountMinor)}</td></tr>`).join('')}</tbody></table></div></section></div>`;
  bindForm('topup','/admin/topups',({amount,...data})=>({...data,amountMinor:minor(amount)}));
}
function renderSettings(content) {
  content.innerHTML = `<div class="grid"><section class="panel"><h2>Пакеты и акции</h2><p class="muted">Простой акционный пакет создаётся с отдельным названием и ценой. Расписание скидок пока не реализовано.</p>${state.packages.map(p=>`<p><strong>${esc(p.name)}</strong> · ${p.minutes} мин · ${money(p.priceMinor)} · ${p.active?'Доступен':'Скрыт'} <button class="secondary" data-edit="${p.id}">Изменить</button></p>`).join('')}<form id="package"><input type="hidden" name="id"><label>Название<input name="name" required maxlength="80"></label><div class="row"><label>Минуты<input name="minutes" type="number" min="1" max="1440" required></label><label>Цена, сом<input name="price" type="number" min="0.01" max="1000000" step="0.01" required></label></div><label><input name="active" type="checkbox" checked>Доступен для покупки</label><button>Сохранить пакет</button><button type="reset" class="secondary">Очистить</button></form></section>${state.user.role==='Owner'?'<section class="panel"><h2>Новый сотрудник</h2><p class="muted">Личный аккаунт позволит различать операции сотрудников. Администратор получает управление тарифами, возвратами и бесплатным временем.</p><form id="staff"><label>Логин<input name="username" required minlength="3" maxlength="32" autocomplete="off"></label><label>Начальный пароль<input name="password" type="password" required minlength="10" maxlength="128" autocomplete="new-password"></label><button>Создать администратора</button></form></section>':''}</div>`;
  bindForm('package','/admin/packages',({id,name,minutes,price,active,operationId})=>({id:id||null,name,minutes:Number(minutes),priceMinor:minor(price),active:active==='on',operationId}));
  if(state.user.role==='Owner') bindForm('staff','/admin/staff',data=>data);
  content.querySelectorAll('[data-edit]').forEach(button=>button.onclick=()=>{
    const p=state.packages.find(x=>x.id===button.dataset.edit), f=document.querySelector('#package');
    f.elements.id.value=p.id;f.elements.name.value=p.name;f.elements.minutes.value=p.minutes;f.elements.price.value=(p.priceMinor/100).toFixed(2);f.elements.active.checked=!!p.active;
    delete f.dataset.operationId;f.elements.name.focus();
  });
}
function renderCustomer() {
  const active = state.sessions.find(x=>['Running','Paused'].includes(x.status));
  root.innerHTML = `<div class="intro"><div><h1>Ваше время в клубе</h1><p class="muted">Выберите место и пакет. Баланс пополняет администратор.</p></div><div class="metric"><span>Ваш баланс</span><strong>${money(state.user.balanceMinor)}</strong></div></div><div id="offline" hidden>Нет связи с сервером. Покупка недоступна. Отображается последний известный остаток.</div>${active ? `<section class="panel"><span class="badge ${active.status==='Paused'?'paused':''}">${statuses[active.status]}</span><h2>${esc(active.computerName)} · ${esc(active.packageName)}</h2><div class="timer" data-timer="${active.id}"></div><p id="time-warning" class="warning" hidden>Осталось не более пяти минут. В рабочей версии приложения по окончании будут закрыты.</p>${active.status==='Paused'?'<p>Возобновить сеанс может администратор.</p>':''}</section>` : `<label>Тестовое место<select id="purchase-computer">${options(state.computers.filter(x=>!x.occupied),x=>x.name)}</select></label><div class="grid">${state.packages.map(p=>`<article class="package"><h2>${esc(p.name)}</h2><p class="muted">${p.minutes} минут непрерывно</p><div class="price">${money(p.priceMinor)}</div><button data-buy="${p.id}" ${state.user.balanceMinor<p.priceMinor||!state.computers.some(c=>!c.occupied)?'disabled':''}>Купить и начать</button></article>`).join('')}</div>${state.packages.length?'':'<p class="empty">Администратор пока не добавил пакеты.</p>'}`}`;
  root.querySelectorAll('[data-buy]').forEach(button=>button.onclick=async()=>{
    const computerId=document.querySelector('#purchase-computer').value;
    if(!computerId || !online)return;
    const p=state.packages.find(x=>x.id===button.dataset.buy);
    if(!confirm(`Списать ${money(p.priceMinor)} и сразу начать ${p.minutes} минут?`))return;
    await mutateButton(button,'/purchases',{computerId,packageId:p.id});
  });
}
function bindForm(id,path,transform) {
  const form=document.getElementById(id);
  form.onsubmit=async event=>{
    event.preventDefault(); if(busy)return;
    try {
      const data=transform({...Object.fromEntries(new FormData(form)),operationId:form.dataset.operationId||crypto.randomUUID()});
      form.dataset.operationId=data.operationId;busy=true; form.querySelectorAll('button').forEach(x=>x.disabled=true);
      await api(path,data);form.reset();delete form.dataset.operationId;toast('Операция сохранена.');busy=false;refundOpen=false;await refresh(true);
    } catch(e) { toast(e.message,true);busy=false;form.querySelectorAll('button').forEach(x=>x.disabled=false); }
  };
  form.addEventListener('input',()=>{delete form.dataset.operationId;});
}
async function mutateButton(button,path,data) {
  if(busy)return;busy=true;button.disabled=true;
  button.dataset.operationId ||= crypto.randomUUID();
  try {await api(path,{...data,operationId:button.dataset.operationId});toast('Операция сохранена.');}
  catch(e){toast(e.message,true);}
  finally{busy=false;await refresh(true);}
}
async function sessionAction(button) {
  const reason=prompt('Укажите причину действия. Она попадёт в журнал владельца.');
  if(!reason?.trim())return;
  await mutateButton(button,`/admin/sessions/${button.dataset.session}/${button.dataset.action}`,{reason});
}
function showRefund(id) {
  const session=state.sessions.find(x=>x.id===id);busy=true;refundOpen=true;
  document.querySelector('#content').innerHTML=`<section class="panel"><h2>Ручная выдача компенсации</h2><p>${esc(session.username)} · ${esc(session.computerName)}. Доступно не более ${money(session.availableRefundMinor)}.</p><p class="muted">Деньги выдаются посетителю вне программы. Баланс аккаунта не увеличится.</p><form id="refund"><input type="hidden" name="sessionId" value="${id}"><label>Сумма, сом<input name="amount" type="number" min="0.01" max="${session.availableRefundMinor/100}" step="0.01" value="${(session.availableRefundMinor/100).toFixed(2)}" required></label>${methodField()}<label>Причина<input name="reason" required maxlength="500"></label><button>Подтвердить выдачу денег</button><button type="button" class="secondary" id="cancel-refund">Отмена</button></form></section>`;
  busy=false;
  // Keep this form until the user finishes it; periodic updates must not discard it.
  document.querySelector('#refund input[name=amount]').focus();
  bindForm('refund','/admin/refunds',({amount,...data})=>({...data,amountMinor:minor(amount)}));
  document.querySelector('#cancel-refund').onclick=()=>{refundOpen=false;render();};
}
async function downloadBackup() {
  const button=document.querySelector('#backup');button.disabled=true;
  try {
    const response=await fetch('/api/admin/backups',{method:'POST',headers:{'X-CSRF-TOKEN':token,'Content-Type':'application/json'},body:'{}'});
    if(!response.ok)throw new Error('Не удалось создать копию. Проверьте журнал сервера.');
    const blob=await response.blob(), url=URL.createObjectURL(blob), a=document.createElement('a');
    a.href=url;a.download=`club-backup-${new Date().toISOString().replace(/[:.]/g,'-')}.db`;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
    toast('Копия проверена и передана браузеру. Сохраните файл на флешку и затем извлеките её.');
  }catch(e){toast(e.message,true);}finally{button.disabled=false;}
}
function updateTimers() {
  if(!state)return;
  const offline=document.querySelector('#offline');if(offline)offline.hidden=online;
  document.querySelectorAll('[data-timer]').forEach(element=>{
    const s=state.sessions.find(x=>x.id===element.dataset.timer);if(!s)return;
    const ms=Math.max(0,s.remainingMs-(s.status==='Running'?performance.now()-lastFetch:0));
    const seconds=Math.ceil(ms/1000);element.textContent=`${String(Math.floor(seconds/3600)).padStart(2,'0')}:${String(Math.floor(seconds/60)%60).padStart(2,'0')}:${String(seconds%60).padStart(2,'0')}`;
    if(state.user.role==='Customer'&&s.status==='Running'&&ms<=300000){
      const warning=document.querySelector('#time-warning');if(warning)warning.hidden=false;
      if(!warned.has(s.id)){warned.add(s.id);toast('До окончания сеанса осталось не более пяти минут.');}
    }
  });
  document.querySelectorAll('[data-buy]').forEach(button=>{if(!online)button.disabled=true;});
}
setInterval(()=>{if(state&&!busy)refresh();},5000);
setInterval(updateTimers,1000);
start();
