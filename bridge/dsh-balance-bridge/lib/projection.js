// Whitelist amounts only. Never copy the AccountView, profile, URLs, or credentials.
const decimal = /^-?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:e([+-]?[0-9]+))?$/i;
export function wallets(value) {
  if (!Array.isArray(value) || value.length > 8) throw new Error('wallet schema');
  return value.map(row => {
    if (!row || !['CNY', 'USD'].includes(row.currency) || typeof row.balance !== 'string' || row.balance.length > 256) throw new Error('wallet schema');
    const match = decimal.exec(row.balance);
    if (!match || match[0] !== row.balance || (match[1] !== undefined && Math.abs(Number(match[1])) > 1000)) throw new Error('wallet schema');
    return { currency: row.currency, balance: row.balance };
  });
}
export function project(snapshot, at = new Date().toISOString()) {
  if (snapshot?.view?.status === 'signed-out') return { state: 'signed-out' };
  if (snapshot?.view?.status !== 'credential-stored') return { state: 'unavailable' };
  const balance = snapshot.details?.balance;
  if (balance?.status !== 'ready') return { state: balance?.status === 'failed' ? 'failed' : 'loading' };
  return { state: 'ready', observedAt: at, normal_wallets: wallets(balance.value), bonus_wallets: wallets(balance.bonusWallets) };
}
