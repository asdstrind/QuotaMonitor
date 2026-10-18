using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Services;
namespace QuotaMonitor.ViewModels;
// WPF-free on purpose: the test project compiles this file directly.
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName]string? property=null)=>PropertyChanged?.Invoke(this,new(property));
}
public sealed class ActionCommand(Action action) : ICommand
{
    public bool CanExecute(object? parameter)=>true;
    public void Execute(object? parameter)=>action();
    public event EventHandler? CanExecuteChanged {add{}remove{}}
}
public sealed record MetricRow(string Label,string Value);
public sealed record QuotaGauge(string Label,double Remaining,string Value,string Reset,bool Low);
public sealed record LaneSummary(string Id,string Name,string Accent,string Total,string Status,string Detail,bool HasIssue,bool Pinned=false);
public sealed record TipRow(string Name,string Accent,string Value,string Detail,string? Flag);
public sealed record DayTip(string Title,IReadOnlyList<TipRow> Rows,string? Note);

public static class Format
{
    private static readonly string[] Weekdays=["星期一","星期二","星期三","星期四","星期五","星期六","星期日"];
    // Chinese compact counts: 8,450 / 5.1万 / 8,523万 / 16.79亿.
    public static string Tokens(long value)=>value switch{
        <10_000=>value.ToString("N0",CultureInfo.InvariantCulture),
        <1_000_000=>(value/10_000d).ToString("0.#",CultureInfo.InvariantCulture)+"万",
        <100_000_000=>(value/10_000d).ToString("N0",CultureInfo.InvariantCulture)+"万",
        _=>(value/100_000_000d).ToString("0.##",CultureInfo.InvariantCulture)+"亿"};
    public static string Usd(decimal value)=>value is >0m and <0.005m?"<$0.01":"$"+value.ToString("N2",CultureInfo.InvariantCulture);
    public static string Money(decimal value,string? currency)=>currency=="CNY"?(value is >0m and <0.005m?"<¥0.01":"¥"+value.ToString("N2",CultureInfo.InvariantCulture)):Usd(value);
    public static string Day(DateOnly day)=>$"{day.Month}月{day.Day}日 {Weekdays[((int)day.DayOfWeek+6)%7]}";
    public static string Countdown(TimeSpan left)
    {
        if(left<=TimeSpan.Zero)return "等待服务端重置";
        if(left.TotalHours>=24){var days=(int)left.TotalDays;var hours=left.Hours;return hours>0?$"{days} 天 {hours} 小时后重置":$"{days} 天后重置";}
        if(left.TotalHours>=1)return $"{(int)left.TotalHours} 小时 {left.Minutes} 分后重置";
        return $"{Math.Max(1,(int)Math.Ceiling(left.TotalMinutes))} 分钟后重置";
    }
    public static string Window(long? minutes)=>minutes switch{
        null=>"窗口",10080=>"每周",
        {} m when m%1440==0=>$"{m/1440} 天",
        {} m when m%60==0=>$"{m/60} 小时",
        {} m=>$"{m} 分钟"};
    // "$12.34" -> ("$","12",".34"); "¥1,234.56" -> ("¥","1,234",".56"); other shapes stay whole.
    public static (string Symbol,string Major,string Minor) Split(string amount)
    {
        if(amount.Length<2||amount.Contains(' ')||amount[0] is not('$' or '¥'))return ("",amount,"");
        var dot=amount.LastIndexOf('.');
        return dot<0?(amount[..1],amount[1..],""):(amount[..1],amount[1..dot],amount[dot..]);
    }
}

public sealed class ProviderViewModel(ProviderSnapshot snapshot) : Observable
{
    private ProviderSnapshot data=snapshot;
    public ProviderSnapshot Snapshot=>data;
    public string Id=>data.ProviderId;
    public string Name=>Id=="claude"?"ClaudeCN":data.DisplayName;
    public string Accent=>AccentOf(Id);
    public static string AccentOf(string id)=>id switch{"codex"=>"#19A67A","claude"=>"#E2672F","antigravity"=>"#B59AF7",_=>"#4A8AF0"};
    public string Status=>Id=="dsh"&&data.ConnectionStatus!=ConnectionStatus.Disabled?BalanceState switch{DshBalanceState.Ready=>"BALANCE LIVE"+(data.ConnectionStatus==ConnectionStatus.Partial?" · USAGE PARTIAL":""),DshBalanceState.Stale=>"BALANCE STALE",_=>"BALANCE UNAVAILABLE"}:data.ConnectionStatus switch{ConnectionStatus.Ready=>IsStale?"STALE":"LIVE",ConnectionStatus.Partial=>"PARTIAL",ConnectionStatus.Disabled=>"OFF",ConnectionStatus.Stale=>"STALE",ConnectionStatus.Loading=>"LOADING",_=>"UNAVAILABLE"};
    private DshBalanceState BalanceState=>data.AccountBalance?.At(DateTimeOffset.UtcNow)??DshBalanceState.Unavailable;
    public bool IsStale=>data.ConnectionStatus==ConnectionStatus.Stale||data.LastUpdated is {} at&&DateTimeOffset.UtcNow-at>TimeSpan.FromMinutes(Id=="dsh"?4:Id=="antigravity"?3:7);
    public string Compact=>data.ConnectionStatus==ConnectionStatus.Disabled?"—":Id=="dsh"?BalanceState==DshBalanceState.Ready?DshBalancePresentation.Normal(data.AccountBalance?.Balance):BalanceState==DshBalanceState.Stale?"Stale":"—":IsStale?Id=="antigravity"?"Stale":"—":data.ConnectionStatus==ConnectionStatus.Loading||data.ConnectionStatus==ConnectionStatus.Unavailable?"—":Id=="antigravity"?string.Join(" / ",new[]{"gemini-weekly","3p-weekly"}.Select(id=>data.Windows.FirstOrDefault(w=>w.Bucket==id) is {} w?$"{w.RemainingPercent:0}%":"—")):Id=="codex"?data.RemainingPercent is {} p?$"{p:0}%":"—":data.RemainingBalance is {} b?$"{(data.Currency=="CNY"?"¥":"$")}{b:0.00}":"—";
    public string CompactUpdated=>Id=="dsh"&&data.AccountBalance?.Balance is {} balance?balance.ObservedAt.LocalDateTime.ToString("HH:mm:ss"):"";
    public string CompactTooltip=>Tooltip;
    public string Hero=>Id=="dsh"?DshBalancePresentation.Normal(data.AccountBalance?.Balance):Id=="codex"?data.RemainingPercent is {} p?$"{p:0}%":"—":data.RemainingBalance is {} b?$"${b:0.00}":"—";
    public string Updated=>Id=="dsh"?data.AccountBalance?.Balance is {} balance?$"余额同步 {balance.ObservedAt.LocalDateTime:MM-dd HH:mm:ss}":"余额尚未同步":data.LastUpdated is {} at?$"{(Id=="antigravity"?"本地读取":"更新")} {at.LocalDateTime:HH:mm:ss}" : "尚未更新";
    public string Error=>string.Join("\n",new[]{data.AccountBalance?.Error,data.ErrorMessage}.Where(x=>!string.IsNullOrEmpty(x)));
    public bool HasError=>!string.IsNullOrEmpty(Error);
    public IEnumerable<MetricRow> Rows=>Id switch{
        "claude"=>[new("今日使用费用",Money(data.TodayCost)),new("累计使用费用",Money(data.TotalCost)),new("今日请求",Tokens(data.TodayRequests)),new("今日 Token",Tokens(data.TodayTokens)),new("统计更新",data.UsageUpdated?.LocalDateTime.ToString("HH:mm:ss")??"—")],
        "dsh"=>[new("充值钱包 · 原始金额",DshBalancePresentation.Exact(data.AccountBalance?.Balance?.NormalWallets)),new("赠送钱包 · 原始金额",DshBalancePresentation.Exact(data.AccountBalance?.Balance?.BonusWallets)),new("今日已记录 Token",Tokens(data.TodayTokens)),new("累计 Token",Tokens(data.TotalTokens)),new("今日输入含缓存",Tokens(data.InputTokens)),new("今日输出",Tokens(data.OutputTokens)),new("今日费用",data.TodayCost is {} c?$"Estimated {data.Currency} {c:0.000000}":"Unavailable"),new("最近使用模型",data.CurrentModel??"—"),new("最新用量记录",data.UsageUpdated?.LocalDateTime.ToString("MM-dd HH:mm")??"—")],
        _=>[]};

    // Expanded instrument strip.
    public string StateText=>data.ConnectionStatus==ConnectionStatus.Disabled?"已关闭":data.NeedsSetup?"未配置":Id=="dsh"?BalanceState switch{
            DshBalanceState.Ready=>data.AccountBalance!.Balance!.ObservedAt.LocalDateTime.ToString("HH:mm"),
            DshBalanceState.Stale=>"已过期",
            _=>data.ConnectionStatus==ConnectionStatus.Loading?"读取中":"不可用"}
        :data.ConnectionStatus switch{
            ConnectionStatus.Loading=>"读取中",
            ConnectionStatus.Unavailable=>"不可用",
            _ when IsStale=>"已过期",
            ConnectionStatus.Partial=>"部分可用",
            _=>data.LastUpdated?.LocalDateTime.ToString("HH:mm")??"读取中"};
    public bool StateIsWarning=>data.ConnectionStatus!=ConnectionStatus.Disabled&&(data.NeedsSetup||(Id=="dsh"?BalanceState!=DshBalanceState.Ready&&data.ConnectionStatus!=ConnectionStatus.Loading:data.ConnectionStatus is ConnectionStatus.Unavailable or ConnectionStatus.Partial||IsStale));
    // Stale or failed values stay visible in the expanded view, dimmed, until a fresh read succeeds.
    private bool Fresh=>Id=="dsh"?BalanceState==DshBalanceState.Ready:data.ConnectionStatus is ConnectionStatus.Ready or ConnectionStatus.Partial&&!IsStale;
    public double ValueOpacity=>Fresh?1:0.45;
    public IReadOnlyList<QuotaGauge> Gauges
    {
        get
        {
            if(Id=="antigravity")return new[]{("gemini-weekly","Gemini"),("3p-weekly","Claude/GPT")}.Select(pair=>{
                var w=data.Windows.FirstOrDefault(w=>w.Bucket==pair.Item1);
                return new QuotaGauge(pair.Item2,w?.RemainingPercent??0,w is null?"—":w.RemainingPercent.ToString("0",CultureInfo.InvariantCulture),w?.ResetAt is {} r?$"{r.LocalDateTime:MM-dd HH:mm} 重置":"重置时间未知",w is not null&&w.RemainingPercent<=15);
            }).ToArray();
            if(data.Windows.Count==0)return [];
            var bucket=data.Windows.Any(w=>w.Bucket=="codex")?"codex":data.Windows[0].Bucket;
            var now=DateTimeOffset.UtcNow;
            return data.Windows.Where(w=>w.Bucket==bucket).OrderBy(w=>w.Name=="Primary"?0:1).Take(2)
                .Select(w=>new QuotaGauge(Format.Window(w.DurationMinutes),w.RemainingPercent,w.RemainingPercent.ToString("0",CultureInfo.InvariantCulture),w.ResetAt is {} r?Format.Countdown(r-now):"重置时间未知",w.RemainingPercent<=15)).ToArray();
        }
    }
    public bool HasGauges=>data.Windows.Count>0||Id=="antigravity";
    public bool IsQuota=>Id is "codex" or "antigravity";
    public string EmptyText=>data.ConnectionStatus==ConnectionStatus.Disabled?"已在设置中关闭":data.ConnectionStatus==ConnectionStatus.Loading?"正在读取额度":"暂无额度数据";
    private string? AmountText=>Id switch{
        "claude"=>data.RemainingBalance is {} b?Format.Usd(b):null,
        "dsh"=>data.AccountBalance?.Balance is {} balance&&balance.NormalWallets.Count>0?DshBalancePresentation.Normal(balance):null,
        _=>null};
    public bool HasAmount=>AmountText is not null;
    public string AmountSymbol=>AmountText is {} a?Format.Split(a).Symbol:"";
    public string AmountMajor=>AmountText is {} a?Format.Split(a).Major:"—";
    public string AmountMinor=>AmountText is {} a?Format.Split(a).Minor:"";
    public string AmountCaption=>Id=="dsh"?"充值余额":"账户余额";
    public string DetailLine=>Id switch{
        "claude"=>data.TodayCost is {} c?"今日已用 "+Format.Usd(c):"",
        "dsh"=>data.AccountBalance?.Balance is {} balance?balance.BonusWallets.Count==0?"无赠送余额":"赠送 "+string.Join(" + ",balance.BonusWallets.Select(DshBalancePresentation.Wallet)):"",
        _=>""};
    public string Tooltip
    {
        get
        {
            var lines=new List<string>();
            if(Id is "codex" or "antigravity"){
                lines.Add(Status);
                foreach(var w in data.Windows)lines.Add($"{w.Bucket} {Format.Window(w.DurationMinutes)}：剩余 {w.RemainingPercent:0.#}%（已用 {w.UsedPercent:0.##}%）"+(w.ResetAt is {} r?$"，{r.LocalDateTime:MM-dd HH:mm} 重置":""));
                lines.Add(Updated);
                if(Id=="antigravity")lines.Add("仅两项 Weekly 额度池。服务端未提供数据生成时间；本地读取时间表示响应取得时间。Token 历史独立读取，不按额度反推。");
            }else if(Id=="claude"){
                if(data.RemainingBalance is {} b)lines.Add($"账户余额 ${b:0.000000}");
                if(data.TodayCost is {} c)lines.Add($"今日使用费用 ${c:0.000000}");
                if(data.TodayRequests is {} q)lines.Add($"今日请求 {q:N0} 次");
                if(data.TodayTokens is {} t)lines.Add($"今日 Token {t:N0}");
                if(data.TotalCost is {} total)lines.Add($"累计使用费用 ${total:0.000000}");
                if(data.BillingPreference is {} pref)lines.Add("结算偏好 "+pref);
                lines.Add("使用费用按 quota 比例换算，不等同于钱包实际扣款。");
                lines.Add(data.LastUpdated is {} at?$"更新于 {at.LocalDateTime:HH:mm:ss}":"尚未成功读取");
            }else{
                lines.Add("充值钱包\n"+DshBalancePresentation.Exact(data.AccountBalance?.Balance?.NormalWallets));
                lines.Add("赠送钱包\n"+DshBalancePresentation.Exact(data.AccountBalance?.Balance?.BonusWallets));
                lines.Add(Updated);
            }
            if(HasError)lines.Add(Error);
            return string.Join("\n",lines);
        }
    }

    public void Update(ProviderSnapshot value){data=value;Tick();}
    public void Tick(){foreach(var p in new[]{nameof(Status),nameof(Compact),nameof(CompactUpdated),nameof(CompactTooltip),nameof(Hero),nameof(Updated),nameof(Error),nameof(HasError),nameof(Rows),nameof(StateText),nameof(StateIsWarning),nameof(ValueOpacity),nameof(Gauges),nameof(HasGauges),nameof(EmptyText),nameof(HasAmount),nameof(AmountSymbol),nameof(AmountMajor),nameof(AmountMinor),nameof(DetailLine),nameof(Tooltip)})Changed(p);}
    private static string Tokens(long? count)=>count?.ToString("N0")??"—";
    private static string Money(decimal? value)=>value is {} v?$"${v:0.000000}":"—";
}

public sealed class MonitorViewModel : Observable
{
    public IReadOnlyList<LowQuotaAlert> Alerts {get;private set;}=[];
    public string MinimalTooltip=>Alerts.Count==0?"靠近显示胶囊 · 点击胶囊展开":"低额度提醒\n"+string.Join("\n",Alerts.Select(a=>a.Description))+"\n靠近显示胶囊 · 点击展开";
    public string FiveHourLight=>Light("codex-5h");
    public string WeeklyLight=>Light("codex-week");
    public string ClaudeLight=>Light("claude");
    public string DshLight=>Light("dsh");
    public string FiveHourMark=>Mark("codex-5h");
    public string WeeklyMark=>Mark("codex-week");
    public string ClaudeMark=>Mark("claude");
    public string DshMark=>Mark("dsh");
    private string Light(string key)=>Alerts.FirstOrDefault(a=>a.Key==key)?.Color??"#343941";
    private string Mark(string key)=>Alerts.FirstOrDefault(a=>a.Key==key)?.Label??"";
    public void UpdateAlerts(MonitorSettings settings,DateTimeOffset? now=null)
    {
        var next=LowQuotaAlerts.Evaluate(Providers.Select(p=>p.Snapshot),settings,now??DateTimeOffset.UtcNow);
        if(Alerts.SequenceEqual(next))return;
        Alerts=next;
        Changed(nameof(MinimalAlerts));
        foreach(var property in new[]{nameof(Alerts),nameof(MinimalTooltip),nameof(FiveHourLight),nameof(WeeklyLight),nameof(ClaudeLight),nameof(DshLight),nameof(FiveHourMark),nameof(WeeklyMark),nameof(ClaudeMark),nameof(DshMark)})Changed(property);
    }
    public IReadOnlyList<LowQuotaAlert> MinimalAlerts=>LowQuotaAlerts.Minimal(Alerts);
    public ObservableCollection<ProviderViewModel> Providers {get;}=[];
    private string[] visibleIds=UsageGrid.Order.ToArray();
    public IReadOnlyList<ProviderViewModel> VisibleProviders=>Providers.Where(p=>visibleIds.Contains(p.Id)).ToArray();
    public bool HasVisibleProviders=>VisibleProviders.Count>0;
    public bool NoVisibleProviders=>!HasVisibleProviders;
    public void ApplyVisibility(MonitorSettings settings,DateOnly today)
    {
        visibleIds=UsageGrid.Order.Where(settings.Visible).ToArray();
        if(hoverLane is not null&&!visibleIds.Contains(hoverLane))hoverLane=null;
        if(pinnedLane is not null&&!visibleIds.Contains(pinnedLane))pinnedLane=null;
        Rebuild(today);Tip=null;
        Changed(nameof(VisibleProviders));Changed(nameof(HasVisibleProviders));Changed(nameof(NoVisibleProviders));Changed(nameof(HoverLane));Changed(nameof(FocusLane));IntensityChanged();Changed(nameof(HeatmapMode));UpdateAlerts(settings);
    }
    public ProviderViewModel Codex=>Providers.First(p=>p.Id=="codex");
    public ProviderViewModel Claude=>Providers.First(p=>p.Id=="claude");
    public ProviderViewModel Dsh=>Providers.First(p=>p.Id=="dsh");
    public ProviderViewModel Antigravity=>Providers.First(p=>p.Id=="antigravity");
    public ICommand RefreshCommand {get;set;}=null!;
    public ICommand SettingsCommand {get;set;}=null!;
    public ICommand ExitCommand {get;set;}=null!;
    private string message="靠近显示胶囊，点击展开，离开自动收起，拖动调整位置";
    public string Message{get=>message;set{message=value;Changed();}}
    private string? notice;
    // A notice is shown only when something needs the user's attention.
    public string? Notice{get=>notice;set{notice=value;Changed();Changed(nameof(HasNotice));}}
    public bool HasNotice=>!string.IsNullOrEmpty(notice);

    private readonly Dictionary<string,UsageHistory> histories=[];
    private DateOnly gridDay;
    public UsageGrid? Grid {get;private set;}
    public IReadOnlyList<LaneSummary> Lanes {get;private set;}=UsageGrid.Order.Select(id=>new LaneSummary(id,NameOf(id),ProviderViewModel.AccentOf(id),"—","读取中","正在读取历史用量。",false)).ToArray();
    public IReadOnlyList<UsageHistory> Histories=>histories.Values.ToArray();
    private bool relative;
    public bool RelativeScale{get=>relative;set{if(relative==value)return;relative=value;Changed();Changed(nameof(UnifiedScale));ScaleChanged?.Invoke(value);}}
    public bool UnifiedScale{get=>!relative;set=>RelativeScale=!value;}
    public event Action<bool>? ScaleChanged;
    private string? hoverLane,pinnedLane;
    public string? HoverLane{get=>hoverLane;set{hoverLane=value is null||visibleIds.Contains(value)?value:null;Changed();Changed(nameof(FocusLane));IntensityChanged();Changed(nameof(HeatmapMode));if(tipDay is {} d)ShowTip(d);}}
    public string? PinnedLane{get=>pinnedLane;set{pinnedLane=value;Lanes=Lanes.Select(l=>l with{Pinned=l.Id==value}).ToArray();Changed();Changed(nameof(FocusLane));IntensityChanged();Changed(nameof(Lanes));}}
    public string? FocusLane=>hoverLane??pinnedLane;
    // The same OKLCH samples as the tile renderer; combined intensity is a neutral key.
    private string Intensity(double level,string neutral)
    {
        if(FocusLane is not {} id)return neutral;
        var (r,g,b)=HeatmapColor.At(id,level);return $"#{r:X2}{g:X2}{b:X2}";
    }
    public string IntensityLow=>Intensity(.25,"#20252A");
    public string IntensityMid=>Intensity(.55,"#4F565F");
    public string IntensityHigh=>Intensity(.9,"#9EA7B3");
    private void IntensityChanged(){Changed(nameof(IntensityLow));Changed(nameof(IntensityMid));Changed(nameof(IntensityHigh));}
    public string HeatmapMode=>FocusLane is {} id?NameOf(id)+" · 单平台 Token":"综合 · 每日总量";
    public void TogglePinnedLane(string id)=>PinnedLane=pinnedLane==id?null:id;
    private DayTip? tip;
    public DayTip? Tip{get=>tip;private set{tip=value;Changed();}}

    public static string NameOf(string id)=>id switch{"codex"=>"Codex","claude"=>"ClaudeCN","antigravity"=>"Antigravity",_=>"DSH"};
    public void SetScale(bool value){relative=value;Changed(nameof(RelativeScale));Changed(nameof(UnifiedScale));}
    public void UpdateHistory(UsageHistory history,DateOnly today){histories[history.ProviderId]=history;Rebuild(today);}
    public void Tick(DateOnly today){if(today!=gridDay)Rebuild(today);}
    public void Rebuild(DateOnly today)
    {
        gridDay=today;
        Grid=UsageGrid.Build(histories.Values,today,visible:visibleIds);
        Lanes=Grid.Providers.Select(Summary).ToArray();
        Changed(nameof(Grid));Changed(nameof(Lanes));
    }
    private LaneSummary Summary(GridProvider p)
    {
        var h=p.History;var window=Grid!.Days.Count;
        string status=h is null?"读取中":h.Error==Core.Services.HistoryCoordinator.Disabled?"已关闭":h.PendingDays>0&&h.Error is null?$"回溯中 {p.CoveredDays}/{window}":h.Error is not null&&p.CoveredDays==0?"不可用":h.Error is not null?"部分失败":"";
        var lines=new List<string>();
        if(h is not null){
            if(h.Scope.Length>0)lines.Add("统计范围："+h.Scope);
            if(h.Source.Length>0)lines.Add("数据来源："+h.Source);
        }
        lines.Add("指标：每日 Token（输入含缓存命中 + 输出）");
        if(p.CoveredDays>0){
            lines.Add($"近 {Grid.Weeks} 周内有记录 {p.CoveredDays} 天，自 {p.CoverageStart:yyyy-MM-dd} 起；其中有用量 {p.ActiveDays} 天");
            lines.Add($"合计 {p.Tokens:N0} Token，{p.Requests:N0} 次请求");
            if(p.Cost is {} cost)lines.Add((h?.CostEstimated==true?"估算费用 ":"使用费用 ")+Format.Money(cost,h?.CostCurrency));
        }else lines.Add("窗口内没有可用记录");
        if(h?.Note is {} note)lines.Add(note);
        if(h?.Error is {} error&&error!=Core.Services.HistoryCoordinator.Disabled)lines.Add(error);
        var total=p.CoveredDays>0?Format.Tokens(p.Tokens):"—";
        if(p.Id=="antigravity")
        {
            if(h?.Error==Core.Services.HistoryCoordinator.Disabled)status="已关闭";
            else if(p.CoveredDays==0)status=h is null?"读取中":"Unavailable";
        }
        return new LaneSummary(p.Id,NameOf(p.Id),ProviderViewModel.AccentOf(p.Id),total,status,string.Join("\n",lines),h?.Error is not null&&h.Error!=Core.Services.HistoryCoordinator.Disabled,p.Id==pinnedLane);
    }
    private GridDay? tipDay;
    public void ShowTip(GridDay? day){tipDay=day;Tip=day is null?null:BuildTip(day);}
    public DayTip BuildTip(GridDay day)
    {
        // Reliability remains in GridLane/UsageHistory; the day panel contains data only.
        var shown=day.Lanes.Where(l=>visibleIds.Contains(l.ProviderId)).ToArray();
        var rows=shown.Select(l=>new TipRow(NameOf(l.ProviderId),ProviderViewModel.AccentOf(l.ProviderId),
            l.Tokens is {} n?n.ToString("N0",CultureInfo.InvariantCulture):"—","",null)).ToArray();
        var total=GridDayTotal(shown);
        return new DayTip(Format.Day(day.Date)+(day.IsToday?"  今天":""),rows,
            total is {} t?$"合计 {t:N0} Token":"合计 —");
    }
    private static long? GridDayTotal(IEnumerable<GridLane> lanes)
    {
        var known=lanes.Where(l=>l.Tokens is not null).ToArray();if(known.Length==0)return null;
        try{return known.Aggregate(0L,(sum,l)=>checked(sum+l.Tokens!.Value));}catch(OverflowException){return null;}
    }
}
