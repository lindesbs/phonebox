import { API, LOCALE, PANEL_HEADERS, api, h, haptic, icon, t, toast, tp } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// ---------------------------------------------------------------- Nextcloud tasks (read-only, over CalDAV by the agent)
// The open tasks of a Nextcloud account; the account stays in the widget's options on the PC. With "Open tasks on the PC"
// on, a tap opens the task in Nextcloud in the PC's browser (the agent checks this widget's option again).

/** A due date for the list: the time today, the weekday this week, else the date; all-day dates are UTC midnights. */
export function fmtDue(due, allDay, now = Date.now()) {
  const d = new Date(due);
  const opts = allDay ? { timeZone: 'UTC' } : {};
  const day = (x, o) => x.toLocaleDateString('en-CA', o); // yyyy-mm-dd, for comparing days
  const today = day(new Date(now), {});
  if (day(d, opts) === today) return allDay ? t('time.today') : d.toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' });
  const days = (due - now) / 86_400_000;
  if (days > 0 && days < 6) return d.toLocaleDateString(LOCALE, { ...opts, weekday: 'short' });
  return d.toLocaleDateString(LOCALE, { ...opts, day: 'numeric', month: 'short' });
}

W['nextcloud-tasks'] = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
    const showList = o.showList !== false, showDue = o.showDue !== false;
    const list = h('div', 'fd-list', h('div', 'empty', t('w.loading')));
    const sub = h('span', 'w-sub', '');
    box.style.setProperty('--c', '#0082c9');
    if (o.title !== '') box.append(h('div', 'w-title', o.title || tp('Tasks'), sub));
    box.append(list);
    let data = null, lastKey = '', busy = false;

    const open = async (task) => {
      if (busy || o.open !== true || !data?.canOpen) return;
      haptic();
      busy = true;
      try {
        const r = await fetch(`${API}/nctasks/open?${wq}&id=${encodeURIComponent(task.id)}`, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS, signal: AbortSignal.timeout?.(8000) });
        const j = await r.json().catch(() => ({}));
        if (r.ok) toast(tp('Opening on the PC'), 'ok'); else toast(j.error || tp('Could not open it'), 'err');
      } catch (_) { toast(t('panel.pcNoAnswer'), 'err'); }
      finally { busy = false; }
    };

    const render = () => {
      const tasks = data?.tasks || [];
      const canOpen = o.open === true && !!data?.canOpen;
      const key = JSON.stringify([!!data, canOpen, tasks.map((x) => x.id + x.title + x.due), data?.error, Math.floor(Date.now() / 60_000)]);
      if (key === lastKey) return;
      lastKey = key;
      sub.replaceChildren(...(data?.error ? [icon('warn')] : []), tasks.length ? String(tasks.length) : '');
      sub.title = data?.error || '';
      if (!data) return;
      if (!tasks.length) { list.replaceChildren(h('div', 'empty', data.error || tp('No open tasks'))); return; }
      const top = list.scrollTop;
      const now = Date.now();
      list.replaceChildren(...tasks.map((x) => {
        const overdue = x.due != null && (x.allDay ? x.due + 86_400_000 : x.due) < now;
        const due = showDue && x.due != null ? h('span', overdue ? 'nct-late' : null, fmtDue(x.due, x.allDay, now)) : null;
        const meta = [due, showList ? x.list : null].filter(Boolean).flatMap((m, i) => (i ? [' · ', m] : [m]));
        const row = h('div', 'fd-i' + (canOpen ? ' link' : ''), h('div', 'fd-t', x.priority >= 1 && x.priority <= 4 ? '❗ ' : '', x.title || '—'),
          meta.length ? h('div', 'fd-m', ...meta) : null);
        row.onclick = () => open(x);
        return row;
      }));
      list.scrollTop = top;
    };

    const load = every(60_000, async () => {
      try { data = await api('/nctasks?' + wq, 'GET', 25_000); lastKey = ''; render(); }
      catch (_) { if (!data) list.replaceChildren(h('div', 'empty', t('w.offline'))); return false; }
    });
    return { update() { load(); render(); } };
  },
};
