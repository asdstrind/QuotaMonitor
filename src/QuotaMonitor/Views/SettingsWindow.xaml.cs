using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using QuotaMonitor.Configuration;
using QuotaMonitor.Core.Adapters;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Security;
using QuotaMonitor.Services;
namespace QuotaMonitor.Views;
public partial class SettingsWindow : Window
{
    private readonly MonitorSettings original;
    private readonly SettingsStore store;
    private readonly CredentialStore secrets;
    private readonly HttpClient http;
    private readonly Action saved;
    private readonly bool startupAllowed;
    private readonly Func<string?>? currentModel;
    private readonly CancellationTokenSource stop=new();
    private bool removeToken;
    private bool displayReady;
    private string page="General", provider="Claude";
    public SettingsWindow(MonitorSettings settings,SettingsStore store,CredentialStore secrets,HttpClient http,Action saved,Func<string?>? currentModel=null,bool startupAllowed=true)
    {
        InitializeComponent();original=settings;this.store=store;this.secrets=secrets;this.http=http;this.saved=saved;this.currentModel=currentModel;this.startupAllowed=startupAllowed;
        FiveHourThreshold.Text=Format(settings.CodexFiveHourLowPercent);WeeklyThreshold.Text=Format(settings.CodexWeeklyLowPercent);ClaudeThreshold.Text=Format(settings.ClaudeLowBalanceUsd);DshThreshold.Text=Format(settings.DshLowBalance);SelectCurrency(DshAlertCurrencyBox,settings.DshAlertCurrency);
        ShowCodexCheck.IsChecked=settings.ShowCodex;ShowClaudeCheck.IsChecked=settings.ShowClaude;ShowDshCheck.IsChecked=settings.ShowDsh;ShowAntigravityCheck.IsChecked=settings.ShowAntigravity;
        AntigravityOn.IsChecked=settings.AntigravityEnabled;AntigravityGeminiThreshold.Text=Format(settings.AntigravityGeminiLowPercent);AntigravityClaudeGptThreshold.Text=Format(settings.AntigravityClaudeGptLowPercent);AntigravityInterval.Text=Format(settings.AntigravityRefreshSeconds);
        CodexOn.IsChecked=settings.CodexEnabled;ClaudeOn.IsChecked=settings.ClaudeEnabled;DshOn.IsChecked=settings.DshEnabled;CenterOn.IsChecked=settings.TopCenter;FullscreenOn.IsChecked=settings.HideInFullscreen;StartupOn.IsChecked=startupAllowed?StartupService.IsEnabled():settings.AutoStart;
        CodexInterval.Text=Format(settings.CodexRefreshSeconds);ClaudeInterval.Text=Format(settings.ClaudeRefreshSeconds);DshInterval.Text=Format(settings.DshRefreshSeconds);UserIdBox.Text=settings.ClaudeUserId;RatioBox.Text=Format(settings.QuotaPerUsd);CodexPathBox.Text=settings.CodexExecutable;DshPathBox.Text=settings.DshSessionRoot;
        EstimateOn.IsChecked=settings.DshEstimateEnabled;PriceModel.Text=settings.DshPricedModel;PriceBasis.Text=settings.DshPriceBasis;PriceInput.Text=Format(settings.DshInputPerMillion);PriceRead.Text=Format(settings.DshCacheReadPerMillion);PriceWrite.Text=Format(settings.DshCacheWritePerMillion);PriceOutput.Text=Format(settings.DshOutputPerMillion);SelectCurrency(PriceCurrency,settings.DshPriceCurrency);
        UpdateCredentialLabel();GeneralNav.IsChecked=true;ClaudeTab.IsChecked=true;
        displayReady=true;ApplyDisplayFilter();
        Loaded+=(_,_)=>{var screen=System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(this);MaxHeight=Math.Max(360,screen.WorkingArea.Height/dpi.DpiScaleY-24);Height=Math.Min(Height,MaxHeight);MaxWidth=Math.Max(620,screen.WorkingArea.Width/dpi.DpiScaleX-24);Width=Math.Min(Width,MaxWidth);Left=Math.Clamp(Left,screen.WorkingArea.Left/dpi.DpiScaleX,(screen.WorkingArea.Right/dpi.DpiScaleX)-Width);Top=Math.Clamp(Top,screen.WorkingArea.Top/dpi.DpiScaleY,(screen.WorkingArea.Bottom/dpi.DpiScaleY)-Height);};
        Closed+=(_,_)=>{stop.Cancel();TokenBox.Clear();};
    }
    private static string Format(object? value)=>Convert.ToString(value,CultureInfo.InvariantCulture)??"";
    private void DisplaySelectionChanged(object sender,RoutedEventArgs e){if(displayReady)ApplyDisplayFilter();}
    private void ApplyDisplayFilter()
    {
        var tabs=new[]{(CodexTab,ShowCodexCheck,CodexPanel),(ClaudeTab,ShowClaudeCheck,ClaudePanel),(DshTab,ShowDshCheck,DshPanel),(AntigravityTab,ShowAntigravityCheck,AntigravityPanel)};
        foreach(var (_,check,panel) in tabs)if(check.IsChecked!=true)panel.Visibility=Visibility.Collapsed;
        var visible=tabs.Where(t=>t.Item2.IsChecked==true).ToArray();
        ProviderSelectionEmpty.Visibility=visible.Length==0?Visibility.Visible:Visibility.Collapsed;
        if(visible.Length==0){foreach(var (tab,_,_) in tabs)tab.IsChecked=false;return;}
        if(!visible.Any(t=>t.Item1.IsChecked==true))visible[0].Item1.IsChecked=true;
    }
    private static void SelectCurrency(ComboBox box,string text){box.SelectedItem=box.Items.Cast<ComboBoxItem>().FirstOrDefault(i=>string.Equals(i.Content?.ToString(),text,StringComparison.OrdinalIgnoreCase));}
    private static string Currency(ComboBox box)=>(box.SelectedItem as ComboBoxItem)?.Content?.ToString()??throw new ArgumentException("请选择 CNY 或 USD 币种。");
    private void Navigate(object sender,RoutedEventArgs e)
    {
        if(PageHost is null)return;
        page=(string)((RadioButton)sender).Tag;GeneralPage.Visibility=page=="General"?Visibility.Visible:Visibility.Collapsed;ProvidersPage.Visibility=page=="Providers"?Visibility.Visible:Visibility.Collapsed;AlertsPage.Visibility=page=="Alerts"?Visibility.Visible:Visibility.Collapsed;AdvancedPage.Visibility=page=="Advanced"?Visibility.Visible:Visibility.Collapsed;
        (PageTitle.Text,PageSubtitle.Text)=page switch{"General"=>("保持可见，恰到好处。","桌面行为与 Harness 显示选择"),"Providers"=>("四个平台，一处可见。","管理连接与读取开关"),"Alerts"=>("在需要时，轻轻提醒。","为 Alert Minimal 设置低额度阈值"),_=>("更多控制，更少打扰。","常用配置之外的可选覆盖")};
        PageScroll.ScrollToTop();AnimatePage();
    }
    private void ProviderNavigate(object sender,RoutedEventArgs e)
    {
        if(DshPanel is null)return;provider=(string)((RadioButton)sender).Tag;CodexPanel.Visibility=provider=="Codex"?Visibility.Visible:Visibility.Collapsed;ClaudePanel.Visibility=provider=="Claude"?Visibility.Visible:Visibility.Collapsed;DshPanel.Visibility=provider=="Dsh"?Visibility.Visible:Visibility.Collapsed;
        AntigravityPanel.Visibility=provider=="Antigravity"?Visibility.Visible:Visibility.Collapsed;
        var model=currentModel?.Invoke();DshModelLabel.Text=string.IsNullOrWhiteSpace(model)?"等待首个可验证 Session · 自动识别，无需填写":model+" · 来自最近的 Session";PageScroll.ScrollToTop();AnimatePage();
    }
    private void AnimatePage(){if(SystemParameters.ClientAreaAnimation)PageHost.BeginAnimation(OpacityProperty,new DoubleAnimation(0.7,1,TimeSpan.FromMilliseconds(140)));}
    private void SettingsSizeChanged(object sender,SizeChangedEventArgs e)
    {
        if(Branding is null)return;
        var compact=e.NewSize.Height<520;Branding.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
        foreach(var nav in new[]{GeneralNav,ProvidersNav,AlertsNav,AdvancedNav})nav.Padding=compact?new Thickness(12,9,12,9):new Thickness(14,12,14,12);
    }
    private void HeaderDrag(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left)return;
        for(var node=e.OriginalSource as DependencyObject;node is not null;)
        {
            if(node is System.Windows.Controls.Primitives.ButtonBase)return;
            node=node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D?System.Windows.Media.VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
        }
        DragMove();
    }
    private void WindowKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Escape){Close();e.Handled=true;}else if(e.Key==Key.S&&Keyboard.Modifiers==ModifierKeys.Control){SaveClick(sender,e);e.Handled=true;}}
    private void UpdateCredentialLabel(){try{CredentialLabel.Text=removeToken?"将在保存时移除；输入新 Token 可替换":secrets.Read() is null?"未保存 Token · 输入新 Token":"已安全保存 · 留空保留现有 Token";}catch{CredentialLabel.Text="凭据管理器暂不可用";}}
    private T Field<T>(Control control,Func<T> read)
    {
        try{return read();}catch(ArgumentException){if(control==FiveHourThreshold||control==WeeklyThreshold||control==ClaudeThreshold||control==DshThreshold||control==AntigravityGeminiThreshold||control==AntigravityClaudeGptThreshold)AlertsNav.IsChecked=true;else if(control==UserIdBox){ProvidersNav.IsChecked=true;ClaudeTab.IsChecked=true;}else{AdvancedNav.IsChecked=true;if(control==CodexInterval||control==ClaudeInterval||control==DshInterval||control==AntigravityInterval)RefreshExpander.IsExpanded=true;else if(control==CodexPathBox)PathsExpander.IsExpanded=true;else if(control==RatioBox)RatioExpander.IsExpanded=true;else PriceExpander.IsExpanded=true;}control.BringIntoView();control.Focus();throw;}
    }
    private void ReadClaude(MonitorSettings draft)
    {
        draft.ClaudeUserId=Field(UserIdBox,()=>{var text=UserIdBox.Text.Trim();if(text.Length>0&&(!long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var id)||id<=0))throw new ArgumentException("User ID 必须为正整数。");return text;});
        draft.QuotaPerUsd=Field(RatioBox,()=>decimal.TryParse(RatioBox.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out var n)&&n>0?n:throw new ArgumentException("Quota/USD 必须大于零。"));
    }
    private MonitorSettings ReadForm()
    {
        var draft=JsonSerializer.Deserialize<MonitorSettings>(JsonSerializer.Serialize(original))!;
        draft.ShowCodex=ShowCodexCheck.IsChecked==true;draft.ShowClaude=ShowClaudeCheck.IsChecked==true;draft.ShowDsh=ShowDshCheck.IsChecked==true;draft.ShowAntigravity=ShowAntigravityCheck.IsChecked==true;
        draft.AntigravityEnabled=AntigravityOn.IsChecked==true;draft.AntigravityGeminiLowPercent=Field(AntigravityGeminiThreshold,()=>Percent(AntigravityGeminiThreshold.Text));draft.AntigravityClaudeGptLowPercent=Field(AntigravityClaudeGptThreshold,()=>Percent(AntigravityClaudeGptThreshold.Text));draft.AntigravityRefreshSeconds=Field(AntigravityInterval,()=>Interval(AntigravityInterval.Text));
        draft.CodexFiveHourLowPercent=Field(FiveHourThreshold,()=>Percent(FiveHourThreshold.Text));draft.CodexWeeklyLowPercent=Field(WeeklyThreshold,()=>Percent(WeeklyThreshold.Text));draft.ClaudeLowBalanceUsd=Field(ClaudeThreshold,()=>Threshold(ClaudeThreshold.Text));draft.DshLowBalance=Field(DshThreshold,()=>Threshold(DshThreshold.Text));draft.DshAlertCurrency=Currency(DshAlertCurrencyBox);
        draft.CodexEnabled=CodexOn.IsChecked==true;draft.ClaudeEnabled=ClaudeOn.IsChecked==true;draft.DshEnabled=DshOn.IsChecked==true;draft.TopCenter=CenterOn.IsChecked==true;draft.HideInFullscreen=FullscreenOn.IsChecked==true;draft.AutoStart=StartupOn.IsChecked==true;
        draft.CodexRefreshSeconds=Field(CodexInterval,()=>Interval(CodexInterval.Text));draft.ClaudeRefreshSeconds=Field(ClaudeInterval,()=>Interval(ClaudeInterval.Text));draft.DshRefreshSeconds=Field(DshInterval,()=>Interval(DshInterval.Text));ReadClaude(draft);
        draft.CodexExecutable=Field(CodexPathBox,()=>{var path=CodexPathBox.Text.Trim();if(path.Length>0&&(!File.Exists(path)||!path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("请选择已存在的原生 codex.exe，或留空自动发现。");return path;});
        // A directory that does not exist yet is not a prerequisite for saving or balance monitoring.
        draft.DshSessionRoot=DshPathBox.Text.Trim();draft.DshEstimateEnabled=EstimateOn.IsChecked==true;
        draft.DshPricedModel=PriceModel.Text.Trim();draft.DshPriceBasis=PriceBasis.Text.Trim();draft.DshInputPerMillion=Field(PriceInput,()=>Price(PriceInput.Text));draft.DshCacheReadPerMillion=Field(PriceRead,()=>Price(PriceRead.Text));draft.DshCacheWritePerMillion=Field(PriceWrite,()=>Price(PriceWrite.Text));draft.DshOutputPerMillion=Field(PriceOutput,()=>Price(PriceOutput.Text));draft.DshPriceCurrency=Currency(PriceCurrency);
        return draft;
    }
    private static int Interval(string text)=>int.TryParse(text,out var n)&&n>=30&&n<=3600?n:throw new ArgumentException("刷新间隔应为 30–3600 秒。");
    private static double Percent(string text)=>double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)&&n>=0&&n<=100?n:throw new ArgumentException("额度提醒阈值应为 0–100%，0 关闭提醒。");
    private static decimal Threshold(string text)=>decimal.TryParse(text,NumberStyles.Number,CultureInfo.InvariantCulture,out var n)&&n>=0?n:throw new ArgumentException("余额提醒阈值必须为非负数，0 关闭提醒。");
    private static decimal? Price(string text)=>string.IsNullOrWhiteSpace(text)?null:decimal.TryParse(text,NumberStyles.Number,CultureInfo.InvariantCulture,out var n)&&n>=0?n:throw new ArgumentException("价格必须为非负数或留空。");
    private async void TestClick(object sender,RoutedEventArgs e)
    {
        TestButton.IsEnabled=false;TestResult.Text="正在安全连接…";
        try{var draft=new MonitorSettings();ReadClaude(draft);var token=TokenBox.Password.Length>0?TokenBox.Password:removeToken?null:secrets.Read();if(string.IsNullOrWhiteSpace(token)||!long.TryParse(draft.ClaudeUserId,out var id))throw new ArgumentException("请填写新 Token 和数字 User ID，或使用已保存的 Token。");var result=await new ClaudeCNAdapter(http,draft,secrets).QueryAsync(token,id,true,stop.Token);if(!stop.IsCancellationRequested)TestResult.Text=result.ConnectionStatus==ConnectionStatus.Ready?"连接成功 · 余额与消费已验证":"部分接口可用 · "+result.ErrorMessage;}
        catch(OperationCanceledException){if(!stop.IsCancellationRequested)TestResult.Text="连接超时。";}
        catch(Exception ex){if(!stop.IsCancellationRequested)TestResult.Text=ex is ProviderException or ArgumentException?ex.Message:"连接失败（"+ex.GetType().Name+"）；响应正文已隐藏。";}
        finally{if(!stop.IsCancellationRequested)TestButton.IsEnabled=true;}
    }
    private void SaveClick(object sender,RoutedEventArgs e)
    {
        SaveError.Text="";MonitorSettings draft;bool credentialChanged=false,startupChanged=false;string? previousToken=null;string? previousStartupCommand=null;
        try
        {
            draft=ReadForm();var token=TokenBox.Password;
            if(token.Length>0||removeToken){previousToken=secrets.Read();credentialChanged=true;if(token.Length>0)secrets.Write(token);else secrets.Delete();}
            if(startupAllowed){previousStartupCommand=StartupService.ReadCommand();startupChanged=(previousStartupCommand is not null)!=draft.AutoStart;if(startupChanged)StartupService.Set(draft.AutoStart);}
            store.Save(draft);
        }
        catch(Exception ex)
        {
            // Keep credentials and startup consistent if the settings commit fails.
            bool rollbackFailed=false;
            try{if(credentialChanged){if(previousToken is null)secrets.Delete();else secrets.Write(previousToken);}if(startupChanged)StartupService.RestoreCommand(previousStartupCommand);}catch{rollbackFailed=true;}
            SaveError.Text=(ex is ArgumentException?ex.Message:"保存失败（"+ex.GetType().Name+"），请检查本地权限。")+(rollbackFailed?" 凭据或自启恢复失败，请检查 Windows 设置。":"");return;
        }
        foreach(var property in typeof(MonitorSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(original,property.GetValue(draft));TokenBox.Clear();Close();saved();
    }
    private void DeleteTokenClick(object sender,RoutedEventArgs e){removeToken=true;TokenBox.Clear();UpdateCredentialLabel();}
    private void CancelClick(object sender,RoutedEventArgs e)=>Close();
    public bool SaveForSmoke(){SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));return string.IsNullOrEmpty(SaveError.Text);}
}
