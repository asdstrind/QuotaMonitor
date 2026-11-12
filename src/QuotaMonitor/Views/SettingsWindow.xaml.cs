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
    private string page="General", provider="Go";
    public SettingsWindow(MonitorSettings settings,SettingsStore store,CredentialStore secrets,HttpClient http,Action saved,Func<string?>? currentModel=null,bool startupAllowed=true)
    {
        InitializeComponent();original=settings;this.store=store;this.secrets=secrets;this.http=http;this.saved=saved;this.currentModel=currentModel;this.startupAllowed=startupAllowed;
        FiveHourThreshold.Text=Format(settings.CodexFiveHourLowPercent);WeeklyThreshold.Text=Format(settings.CodexWeeklyLowPercent);GoThreshold.Text=Format(settings.GoFiveHourLowPercent);GoWeeklyThreshold.Text=Format(settings.GoWeeklyLowPercent);GoMonthlyThreshold.Text=Format(settings.GoMonthlyLowPercent);DshThreshold.Text=Format(settings.DshLowBalance);SelectCurrency(DshAlertCurrencyBox,settings.DshAlertCurrency);
        ShowCodexCheck.IsChecked=settings.ShowCodex;ShowGoCheck.IsChecked=settings.ShowGo;ShowDshCheck.IsChecked=settings.ShowDsh;ShowAntigravityCheck.IsChecked=settings.ShowAntigravity;
        AntigravityOn.IsChecked=settings.AntigravityEnabled;AntigravityGeminiThreshold.Text=Format(settings.AntigravityGeminiLowPercent);AntigravityClaudeGptThreshold.Text=Format(settings.AntigravityClaudeGptLowPercent);AntigravityInterval.Text=Format(settings.AntigravityRefreshSeconds);
        CodexOn.IsChecked=settings.CodexEnabled;GoOn.IsChecked=settings.GoEnabled;DshOn.IsChecked=settings.DshEnabled;CenterOn.IsChecked=settings.TopCenter;FullscreenOn.IsChecked=settings.HideInFullscreen;StartupOn.IsChecked=startupAllowed?(settings.AutoStart||StartupService.IsEnabled()):settings.AutoStart;
        if(startupAllowed&&settings.AutoStart&&!StartupService.IsEnabled())SaveError.Text=StartupService.HasCurrentCommand()?"Windows 已禁用开机启动；保存开启状态可重新启用。":"开机启动路径需要更新；保存后将核对当前应用路径。";
        CodexInterval.Text=Format(settings.CodexRefreshSeconds);GoInterval.Text=Format(settings.GoRefreshSeconds);DshInterval.Text=Format(settings.DshRefreshSeconds);CodexPathBox.Text=settings.CodexExecutable;DshPathBox.Text=settings.DshSessionRoot;
        EstimateOn.IsChecked=settings.DshEstimateEnabled;PriceModel.Text=settings.DshPricedModel;PriceBasis.Text=settings.DshPriceBasis;PriceInput.Text=Format(settings.DshInputPerMillion);PriceRead.Text=Format(settings.DshCacheReadPerMillion);PriceWrite.Text=Format(settings.DshCacheWritePerMillion);PriceOutput.Text=Format(settings.DshOutputPerMillion);SelectCurrency(PriceCurrency,settings.DshPriceCurrency);
        UpdateCredentialLabel();GeneralNav.IsChecked=true;GoTab.IsChecked=true;
        displayReady=true;ApplyDisplayFilter();
        Loaded+=(_,_)=>{var screen=System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(this);MaxHeight=Math.Max(360,screen.WorkingArea.Height/dpi.DpiScaleY-24);Height=Math.Min(Height,MaxHeight);MaxWidth=Math.Max(620,screen.WorkingArea.Width/dpi.DpiScaleX-24);Width=Math.Min(Width,MaxWidth);Left=Math.Clamp(Left,screen.WorkingArea.Left/dpi.DpiScaleX,(screen.WorkingArea.Right/dpi.DpiScaleX)-Width);Top=Math.Clamp(Top,screen.WorkingArea.Top/dpi.DpiScaleY,(screen.WorkingArea.Bottom/dpi.DpiScaleY)-Height);};
        Closed+=(_,_)=>{stop.Cancel();TokenBox.Clear();};
    }
    private static string Format(object? value)=>Convert.ToString(value,CultureInfo.InvariantCulture)??"";
    private void DisplaySelectionChanged(object sender,RoutedEventArgs e){if(displayReady)ApplyDisplayFilter();}
    private void ApplyDisplayFilter()
    {
        var tabs=new[]{(CodexTab,ShowCodexCheck,CodexPanel),(GoTab,ShowGoCheck,GoPanel),(DshTab,ShowDshCheck,DshPanel),(AntigravityTab,ShowAntigravityCheck,AntigravityPanel)};
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
        if(DshPanel is null)return;provider=(string)((RadioButton)sender).Tag;CodexPanel.Visibility=provider=="Codex"?Visibility.Visible:Visibility.Collapsed;GoPanel.Visibility=provider=="Go"?Visibility.Visible:Visibility.Collapsed;DshPanel.Visibility=provider=="Dsh"?Visibility.Visible:Visibility.Collapsed;
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
        try{return read();}catch(ArgumentException){if(control==FiveHourThreshold||control==WeeklyThreshold||control==GoThreshold||control==GoWeeklyThreshold||control==GoMonthlyThreshold||control==DshThreshold||control==AntigravityGeminiThreshold||control==AntigravityClaudeGptThreshold)AlertsNav.IsChecked=true;else{AdvancedNav.IsChecked=true;if(control==CodexInterval||control==GoInterval||control==DshInterval||control==AntigravityInterval)RefreshExpander.IsExpanded=true;else if(control==CodexPathBox)PathsExpander.IsExpanded=true;else PriceExpander.IsExpanded=true;}control.BringIntoView();control.Focus();throw;}
    }
    private MonitorSettings ReadForm()
    {
        var draft=JsonSerializer.Deserialize<MonitorSettings>(JsonSerializer.Serialize(original))!;
        draft.ShowCodex=ShowCodexCheck.IsChecked==true;draft.ShowGo=ShowGoCheck.IsChecked==true;draft.ShowDsh=ShowDshCheck.IsChecked==true;draft.ShowAntigravity=ShowAntigravityCheck.IsChecked==true;
        draft.AntigravityEnabled=AntigravityOn.IsChecked==true;draft.AntigravityGeminiLowPercent=Field(AntigravityGeminiThreshold,()=>Percent(AntigravityGeminiThreshold.Text));draft.AntigravityClaudeGptLowPercent=Field(AntigravityClaudeGptThreshold,()=>Percent(AntigravityClaudeGptThreshold.Text));draft.AntigravityRefreshSeconds=Field(AntigravityInterval,()=>Interval(AntigravityInterval.Text));
        draft.CodexFiveHourLowPercent=Field(FiveHourThreshold,()=>Percent(FiveHourThreshold.Text));draft.CodexWeeklyLowPercent=Field(WeeklyThreshold,()=>Percent(WeeklyThreshold.Text));draft.GoFiveHourLowPercent=Field(GoThreshold,()=>Percent(GoThreshold.Text));draft.GoWeeklyLowPercent=Field(GoWeeklyThreshold,()=>Percent(GoWeeklyThreshold.Text));draft.GoMonthlyLowPercent=Field(GoMonthlyThreshold,()=>Percent(GoMonthlyThreshold.Text));draft.DshLowBalance=Field(DshThreshold,()=>Threshold(DshThreshold.Text));draft.DshAlertCurrency=Currency(DshAlertCurrencyBox);
        draft.CodexEnabled=CodexOn.IsChecked==true;draft.GoEnabled=GoOn.IsChecked==true;draft.DshEnabled=DshOn.IsChecked==true;draft.TopCenter=CenterOn.IsChecked==true;draft.HideInFullscreen=FullscreenOn.IsChecked==true;draft.AutoStart=StartupOn.IsChecked==true;
        draft.CodexRefreshSeconds=Field(CodexInterval,()=>Interval(CodexInterval.Text));draft.GoRefreshSeconds=Field(GoInterval,()=>int.TryParse(GoInterval.Text,out var n)&&n>=180&&n<=3600?n:throw new ArgumentException("OpenCode Go 刷新间隔应为 180–3600 秒。"));draft.DshRefreshSeconds=Field(DshInterval,()=>Interval(DshInterval.Text));
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
        try{var token=TokenBox.Password.Length>0?TokenBox.Password:removeToken?null:secrets.Read();if(string.IsNullOrWhiteSpace(token))throw new ArgumentException("请填写 Console 只读 API Key，或使用已保存的凭据。");var test=new OpenCodeGoAdapter(http,new(),new TestCredential(token));var result=await test.RefreshAsync(true,stop.Token);var export=await test.ReadHistoryAsync(null,new(DateOnly.FromDateTime(DateTime.UtcNow),DateOnly.MinValue,true),stop.Token);if(!stop.IsCancellationRequested)TestResult.Text=result.ConnectionStatus==ConnectionStatus.Ready&&export.Error is null?"连接成功 · 额度与 Token 导出已验证":"连接未完整验证 · "+(result.ErrorMessage??export.Error); }
        catch(OperationCanceledException){if(!stop.IsCancellationRequested)TestResult.Text="连接超时。";}
        catch(Exception ex){if(!stop.IsCancellationRequested)TestResult.Text=ex is ProviderException or ArgumentException?ex.Message:"连接失败（"+ex.GetType().Name+"）；响应正文已隐藏。";}
        finally{if(!stop.IsCancellationRequested)TestButton.IsEnabled=true;}
    }
    private sealed class TestCredential(string key):ISecretStore{public string? Read()=>key;public void Write(string value)=>throw new NotSupportedException();public void Delete()=>throw new NotSupportedException();}
    private void SaveClick(object sender,RoutedEventArgs e)
    {
        SaveError.Text="";MonitorSettings draft;bool credentialChanged=false,startupChanged=false;string? previousToken=null;StartupService.StartupState? previousStartup=null;
        try
        {
            draft=ReadForm();var token=TokenBox.Password;
            if(token.Length>0||removeToken){previousToken=secrets.Read();credentialChanged=true;if(token.Length>0)secrets.Write(token);else secrets.Delete();}
            if(startupAllowed){previousStartup=StartupService.Capture();startupChanged=true;StartupService.Set(draft.AutoStart);}
            store.Save(draft);
        }
        catch(Exception ex)
        {
            // Keep credentials and startup consistent if the settings commit fails.
            bool rollbackFailed=false;
            try{if(credentialChanged){if(previousToken is null)secrets.Delete();else secrets.Write(previousToken);}if(startupChanged)StartupService.Restore(previousStartup!);}catch{rollbackFailed=true;}
            SaveError.Text=(ex is ArgumentException or InvalidOperationException?ex.Message:"保存失败（"+ex.GetType().Name+"），请检查本地权限。")+(rollbackFailed?" 凭据或自启恢复失败，请检查 Windows 设置。":"");return;
        }
        foreach(var property in typeof(MonitorSettings).GetProperties().Where(p=>p.CanWrite))property.SetValue(original,property.GetValue(draft));TokenBox.Clear();Close();saved();
    }
    private void DeleteTokenClick(object sender,RoutedEventArgs e){removeToken=true;TokenBox.Clear();UpdateCredentialLabel();}
    private void CancelClick(object sender,RoutedEventArgs e)=>Close();
    public bool SaveForSmoke(){SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));return string.IsNullOrEmpty(SaveError.Text);}
}
