import net from 'node:net';
import { homedir } from 'node:os';
import { createHash } from 'node:crypto';
import { Remote, TypertRemoteService } from '@deepseek-ai/dsh-typert-protocol';
import { wallets } from './projection.js';
import { credentialKey } from '@deepseek-ai/dsh-credentials';
import { BalancePoller } from './poller.js';

const initializers = [];
const pipe = '\\\\.\\pipe\\QuotaMonitor.DshBalance.' + createHash('sha256').update(homedir().toLowerCase()).digest('hex').slice(0, 16);
export const inject = [];
export class BalanceBridge extends TypertRemoteService {
  constructor(ctx, config = {}) {
    super(ctx, 'quotaBalanceBridge');
    for (const initialize of initializers) initialize.call(this);
    this.latest = { protocol: 1, sourceVersion: '0.2.0-rc.2', state: 'loading' };
    this.closed = false;
    this.pipeName = config.pipeName ?? pipe;
    this.delay = 1000;
    ctx.effect(() => {
      this.connect();
      return () => { this.closed = true; clearTimeout(this.timer); this.socket?.destroy(); };
    }, 'balance bridge pipe lifetime');
    ctx.inject(['deepseekAccount'], scope => {
      this.autonomous = true;
      const poller = new BalancePoller(scope.deepseekAccount, snapshot => this.accept(snapshot));
      const accountKey = credentialKey('deepseek-account-platform', 'default');
      scope.on('credentials/record-updated', key => { if (key === accountKey) poller.changed(); });
      scope.on('deepseek-account/signed-out', () => poller.changed());
      scope.effect(() => poller.start(), 'balance bridge background queries');
      scope.effect(() => () => this.accept({ state: 'unavailable' }), 'balance bridge account service lost');
    });
  }
  publish(snapshot) {
    // Legacy UI clients must not overwrite Host results or renew their timestamps.
    return !this.autonomous && this.accept(snapshot);
  }
  accept(snapshot) {
    if (!snapshot || !['ready', 'loading', 'failed', 'signed-out', 'unavailable'].includes(snapshot.state)) return false;
    const next = { protocol: 1, sourceVersion: '0.2.0-rc.2', state: snapshot.state };
    if (snapshot.state === 'ready') {
      const at = Date.parse(snapshot.observedAt);
      if (!Number.isFinite(at) || at > Date.now() + 5000) return false;
      try { Object.assign(next, { observedAt: snapshot.observedAt, normal_wallets: wallets(snapshot.normal_wallets), bonus_wallets: wallets(snapshot.bonus_wallets) }); }
      catch { return false; }
    }
    if (snapshot.state === 'signed-out') this.success = undefined;
    if (snapshot.state === 'ready') this.success = next;
    // Keep ready/sign-out frames compatible with stable protocol-1 monitors.
    // Only stale replay needs a separate status alongside its retained wallets.
    this.latest = snapshot.state !== 'ready' && this.success ? { ...this.success, state: 'stale', queryStatus: snapshot.state } : next;
    this.send();
    return true;
  }
  send() {
    if (!this.socket?.pending && this.socket?.writable) {
      // Replace snapshots; a slow or absent monitor must never block DSH.
      if (this.socket.writableLength > 16384) { this.socket.destroy(); return; }
      this.socket.write(JSON.stringify(this.latest) + '\n');
    }
  }
  connect() {
    if (this.closed) return;
    const socket = this.socket = net.createConnection(this.pipeName);
    socket.setTimeout(3000, () => socket.destroy());
    socket.on('error', () => {}); // No response bodies or diagnostic secrets.
    socket.on('connect', () => { this.delay = 1000; socket.setTimeout(0); this.send(); });
    socket.on('data', data => { if (data.length > 64) socket.destroy(); });
    socket.on('close', () => {
      if (this.socket === socket) this.socket = undefined;
      if (!this.closed) { this.timer = setTimeout(() => this.connect(), this.delay); this.timer.unref(); this.delay = Math.min(30000, this.delay * 2); }
    });
  }
}
// Invoke the public standard decorator API without a TypeScript build dependency.
Remote(BalanceBridge.prototype.publish, { kind: 'method', name: 'publish', private: false, static: false, addInitializer: fn => initializers.push(fn) });
export function apply(ctx) { ctx.plugin(BalanceBridge); }
