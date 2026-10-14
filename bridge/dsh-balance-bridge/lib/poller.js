import { wallets } from './projection.js';

// No HTTP or credential access here: the existing Host account service owns both.
export class BalancePoller {
  constructor(account, publish, options = {}) {
    this.account = account;
    this.publish = publish;
    this.interval = options.interval ?? 180000;
    this.timeout = options.timeout ?? 125000; // Official service permits up to 120s.
    this.retryBase = options.retryBase ?? 30000;
    this.retryMax = options.retryMax ?? 900000;
    this.setTimer = options.setTimer ?? setTimeout;
    this.clearTimer = options.clearTimer ?? clearTimeout;
    this.now = options.now ?? (() => new Date());
    this.client = options.client ?? (() => ({ version: '0.2.0-rc.2', locale: Intl.DateTimeFormat().resolvedOptions().locale, timezoneOffsetSeconds: -new Date().getTimezoneOffset() * 60 }));
    this.epoch = 0;
    this.failures = 0;
    this.stop = new AbortController();
  }
  start() {
    // A restarted Host cannot attest which account produced a previous cache.
    this.publish({ state: 'signed-out' });
    this.schedule(0);
    void this.watch();
    return () => this.dispose();
  }
  schedule(ms) {
    this.clearTimer(this.timer);
    if (this.stop.signal.aborted) return;
    this.timer = this.setTimer(() => void this.refresh(), ms);
    this.timer?.unref?.();
  }
  changed() {
    if (this.stop.signal.aborted) return;
    this.epoch++;
    this.failures = 0;
    // Invalidate before any await, including same-status account replacements.
    this.publish({ state: 'signed-out' });
    this.schedule(0);
  }
  async watch() {
    try {
      let status;
      for await (const state of this.account.watch(this.stop.signal)) {
        if (this.stop.signal.aborted) break;
        if (status !== undefined && state.status !== status) this.changed();
        status = state.status;
      }
    } catch {
      if (!this.stop.signal.aborted) this.publish({ state: 'unavailable' });
    }
  }
  async refresh() {
    if (this.stop.signal.aborted || this.flight) return;
    const epoch = this.epoch;
    // Hold single-flight even after our watchdog: getBalance has no caller signal.
    // Releasing early would stack uncancellable requests inside the official service.
    this.flight = true;
    let timedOut = false, delay = this.interval;
    const current = () => !this.stop.signal.aborted && epoch === this.epoch;
    try {
      const state = await this.account.getState();
      if (!current()) return;
      if (state.status === 'signed-out') { this.publish({ state: 'signed-out' }); return; }
      if (state.status !== 'credential-stored') throw new Error('account unavailable');
      // Do not turn a successful balance stale during a routine refresh.
      this.watchdog = this.setTimer(() => {
        timedOut = true;
        if (current()) this.publish({ state: 'failed' });
      }, this.timeout);
      this.watchdog?.unref?.();
      const result = await this.account.getBalance(this.client());
      if (!current()) return;
      if (timedOut || result?.status !== 'ready') throw new Error('balance unavailable');
      const value = { state: 'ready', observedAt: this.now().toISOString(), normal_wallets: wallets(result.value), bonus_wallets: wallets(result.bonusWallets) };
      // The official service returns null when its captured account lifetime changes.
      // Our epoch additionally prevents already-resolved results crossing record updates.
      if (!current()) return;
      this.publish(value);
      this.failures = 0;
    } catch {
      if (current()) {
        this.publish({ state: 'failed' });
        delay = Math.min(this.retryMax, this.retryBase * 2 ** Math.min(this.failures++, 10));
      }
    } finally {
      this.clearTimer(this.watchdog);
      this.flight = false;
      if (!this.stop.signal.aborted) this.schedule(epoch === this.epoch ? delay : 0);
    }
  }
  dispose() {
    this.stop.abort();
    this.epoch++;
    this.clearTimer(this.timer);
    this.clearTimer(this.watchdog);
  }
}
