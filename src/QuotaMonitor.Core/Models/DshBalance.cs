using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace QuotaMonitor.Core.Models;

public enum DshBalanceState { Unavailable, Ready, Stale }
public sealed record WalletBalance(string Currency, string Balance);
public sealed record DshBalance(IReadOnlyList<WalletBalance> NormalWallets, IReadOnlyList<WalletBalance> BonusWallets, DateTimeOffset ObservedAt, string SourceVersion);
public sealed record DshBalanceReading(DshBalance? Balance, DshBalanceState State, string? Error)
{
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(4);
    public DshBalanceState At(DateTimeOffset now) => Balance is null ? DshBalanceState.Unavailable : State != DshBalanceState.Ready || now-Balance.ObservedAt>Freshness ? DshBalanceState.Stale : DshBalanceState.Ready;
    public static DshBalanceReading Empty => new(null,DshBalanceState.Unavailable,"Balance Bridge 尚未连接；等待 DSH Host 后台余额查询。");
}

public static class DshBalancePresentation
{
    // The source string is retained intact. Only compact presentation follows official UI rounding.
    public static bool ValidAmount(string value) => value.Length is >0 and <=256 && Regex.IsMatch(value,@"^-?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    public static string Wallet(WalletBalance wallet)
    {
        var symbol=wallet.Currency=="CNY"?"¥":"$";
        var parts=wallet.Balance.Split(['e','E']);
        var exponent=0;
        if(parts.Length==2&&(!int.TryParse(parts[1],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out exponent)||Math.Abs((long)exponent)>1000))return $"{wallet.Currency} {wallet.Balance}";
        var number=parts[0];var negative=number.StartsWith('-');number=number.TrimStart('-');
        var dot=number.IndexOf('.');var scale=(dot<0?0:number.Length-dot-1)-exponent;
        var digits=BigInteger.Parse(number.Replace(".",""),CultureInfo.InvariantCulture);
        if(digits.IsZero)return symbol+"0.00";
        var shift=scale-2;BigInteger cents;
        if(shift>0){var divisor=BigInteger.Pow(10,shift);cents=BigInteger.DivRem(digits,divisor,out var remainder);if(negative&&remainder*2>=divisor)cents++;}
        else cents=digits*BigInteger.Pow(10,-shift);
        if(cents.IsZero)return negative?"-"+symbol+"0.01":"<"+symbol+"0.01";
        return (negative?"-":"")+symbol+(cents/100).ToString("N0",CultureInfo.InvariantCulture)+"."+(cents%100).ToString("D2",CultureInfo.InvariantCulture);
    }
    public static string Normal(DshBalance? balance) => balance is null || balance.NormalWallets.Count==0 ? "—" : string.Join(" + ",balance.NormalWallets.Select(Wallet));
    public static string Exact(IReadOnlyList<WalletBalance>? wallets) => wallets is null ? "Unavailable" : wallets.Count==0 ? "无钱包条目" : string.Join("\n",wallets.Select(w=>$"{w.Currency} {w.Balance}"));
}
