window.__ModuleLoader__.load({
  id: 'quota-monitor-dsh-balance-bridge',
  factory: () => ({
    inject: ['slots', 'connection'],
    apply(ctx) {
      if (!('dshDesktop' in globalThis)) return;
      let disposed = false, attached = false, source, release, lastBalance, pending, timer, sending = false, delay = 1000;
      const decimal = /^-?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i;
      const wallets = rows => {
        if (!Array.isArray(rows) || rows.length > 8) throw new Error('wallet schema');
        return rows.map(row => {
          if (!row || !['CNY', 'USD'].includes(row.currency) || typeof row.balance !== 'string' || row.balance.length > 256 || !decimal.test(row.balance)) throw new Error('wallet schema');
          return { currency: row.currency, balance: row.balance };
        });
      };
      const send = async () => {
        if (disposed || sending || !pending) return;
        sending = true;
        const value = pending;
        try {
          const result = await ctx.connection.rpc.call('/api', 'quotaBalanceBridge/publish', { args: { snapshot: value } }, AbortSignal.timeout(2500));
          if (result.ok && result.value === true) { if (pending === value) pending = undefined; delay = 1000; }
        } catch {} finally {
          sending = false;
          if (!disposed && pending) { timer = setTimeout(send, delay); delay = Math.min(30000, delay * 2); }
        }
      };
      const observe = () => {
        try {
          const snapshot = source?.getSnapshot();
          const balance = snapshot?.details?.balance;
          let value;
          if (snapshot?.view?.status === 'signed-out') { lastBalance = undefined; value = { state: 'signed-out' }; }
          else if (snapshot?.view?.status !== 'credential-stored') value = { state: 'unavailable' };
          else if (balance?.status === 'ready') {
            if (balance === lastBalance) return; // Profile/notice updates cannot renew wallet freshness.
            lastBalance = balance;
            value = { state: 'ready', observedAt: new Date().toISOString(), normal_wallets: wallets(balance.value), bonus_wallets: wallets(balance.bonusWallets) };
          } else value = { state: balance?.status === 'failed' ? 'failed' : 'loading' };
          pending = value;
          clearTimeout(timer);
          void send();
        } catch { pending = { state: 'failed' }; void send(); }
      };
      const attach = () => {
        try {
          // Public read-only Slot inspection and the official injected Account snapshot source.
          // settings.section is declared when Settings is mounted. The account launcher
          // and quota-notice bindings expose the same snapshot while Settings is closed.
          let next;
          for (const key of ['settings.section', 'settings.launcher', 'shell.quota-notice']) {
            for (const entry of ctx.slots.entriesOfSlot(key)) {
              if (entry.locale !== 'settings.account') continue;
              const binding = entry.inject?.();
              next = binding?.hooks?.account;
              if (typeof next?.getSnapshot === 'function' && typeof next?.subscribe === 'function') break;
              next = undefined;
            }
            if (next) break;
          }
          if (attached && next === source) return;
          attached = true;
          release?.(); source = next; lastBalance = undefined;
          release = source?.subscribe(observe);
          observe();
        } catch { pending = { state: 'unavailable' }; void send(); }
      };
      ctx.effect(() => {
        const stops = ['settings.section', 'settings.launcher', 'shell.quota-notice'].map(key => ctx.slots.subscribe(key, attach));
        attach();
        return () => { disposed = true; clearTimeout(timer); stops.forEach(stop=>stop()); release?.(); };
      }, 'balance bridge passive snapshot observer');
    }
  })
});
