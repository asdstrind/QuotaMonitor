using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using QuotaMonitor.Core.Configuration;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Services;
using QuotaMonitor.ViewModels;
namespace QuotaMonitor.Views;
public partial class IslandWindow : Window
{
    public const double MinimalWidth=176, MinimalHeight=8, AlertHeight=26, CollapsedWidth=520, ExpandedWidth=720, CollapsedHeight=82;
    private readonly MonitorSettings settings;
    private readonly Action save;
    private readonly IslandInteraction interaction=new();
    private readonly Stopwatch clock=Stopwatch.StartNew();
    private readonly DispatcherTimer pointer=new(){Interval=TimeSpan.FromMilliseconds(24)};
    private readonly DispatcherTimer fullscreen=new(){Interval=TimeSpan.FromSeconds(2)};
    private readonly MorphMotion widthMotion=new(176),heightMotion=new(8),expansion=new(0),alertMotion=new(0);
    private readonly TranslateTransform compactTransform=new(),detailsTransform=new();
    private IntPtr handle;
    private bool dragged,dragging,animating,fullscreenHidden,updatingFrame;
    private bool detailsReady;
    private TimeSpan lastRenderingTime;
    private double expandedHeight;
    private IslandState appliedState=IslandState.Minimal;
    private Point? down;
    private Rect? exitBounds;
    private IslandState exitState;
    internal bool VerificationSuspendsPointer { get; set; }
    internal bool VerificationCapturing { get; set; }
    internal bool RecordMotion { get; set; }
    internal List<MotionSample> MotionSamples { get; }=[];
    public sealed record MotionSample(double Ms,double FrameMs,double CpuMs,double Width,double Height,double Amount,double Alert,double X,double Y,int Layouts,int NativePositions);
    private int layouts=>ContentSurface.Measures+ContentSurface.Arranges; private int nativePositions;
    internal Rect VisibleBounds=>Island.ShellBounds;
    private double paintedWidth=176,paintedHeight=8;
    public double VisualWidth=>paintedWidth;
    public double VisualHeight=>paintedHeight;
    public object MorphEvidence=>new { Width=VisualWidth,Height=VisualHeight,Amount=expansion.Value,ShellOpacity=Island.Opacity,Shells=1,Bounds=Island.ShellBounds,Animating=animating,HostWidth=Width,HostHeight=Height,Layouts=layouts,NativePositions=nativePositions,ShadowBuilds=Island.ShadowBuilds };
    public IslandState State=>interaction.State;
    public bool IsExpanded=>State==IslandState.Expanded;
    public bool NativeVisible=>handle!=IntPtr.Zero&&NativeWindow.IsWindowVisible(handle);
    public object WindowEvidence{get{NativeWindow.GetWindowRect(handle,out var r);return new{Visible=NativeVisible,Bounds=r,VisualBounds=Island.ShellBounds,DpiScale=NativeWindow.Scale(handle),State=State.ToString()};}}
    private MonitorViewModel Vm=>(MonitorViewModel)DataContext;
    public IslandWindow(MonitorViewModel vm,MonitorSettings settings,Action save)
    {
        InitializeComponent();DataContext=vm;this.settings=settings;this.save=save;
        Compact.RenderTransform=compactTransform;Details.RenderTransform=detailsTransform;
        vm.UpdateAlerts(settings);vm.PropertyChanged+=AlertsChanged;
        pointer.Tick+=(_,_)=>PollPointer();fullscreen.Tick+=(_,_)=>UpdateFullscreen();
        SourceInitialized+=(_,_)=>{handle=new WindowInteropHelper(this).Handle;NativeWindow.ToolWindow(handle);NativeWindow.DisableNonClientRendering(handle);HwndSource.FromHwnd(handle)?.AddHook(InputHook);ConfigureHost();ApplyState(false);fullscreen.Start();pointer.Start();};
        Loaded+=(_,_)=>NativeWindow.ShowWithoutActivating(handle);

        SystemEvents.DisplaySettingsChanged+=DisplayChanged;
        DpiChanged+=(_,_)=>Dispatcher.BeginInvoke(RefreshGeometry);
        Closed+=(_,_)=>{pointer.Stop();fullscreen.Stop();CompositionTarget.Rendering-=MotionFrame;SystemEvents.DisplaySettingsChanged-=DisplayChanged;vm.PropertyChanged-=AlertsChanged;};
    }
    private double Now=>clock.Elapsed.TotalSeconds;
    private void AlertsChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName!=nameof(MonitorViewModel.MinimalAlerts)||handle==IntPtr.Zero)return;
        AlertStrip.Update(Vm.MinimalAlerts,Now,SystemParameters.ClientAreaAnimation);
        if(State==IslandState.Minimal)ApplyState();else StartFrames();
    }
    private void UpdateFullscreen()
    {
        var hidden=settings.HideInFullscreen&&NativeWindow.IsFullscreen(handle)&&!OwnedWindows.Cast<Window>().Any(w=>w.IsVisible);
        if(hidden&&!fullscreenHidden){interaction.Escape();ApplyState(false);}
        fullscreenHidden=hidden;Opacity=hidden?0:1;IsHitTestVisible=!hidden;UpdateInputTransparency();
    }
    private bool Suspended=>VerificationSuspendsPointer||fullscreenHidden||!IsVisible||dragging||down is not null||ContextMenu?.IsOpen==true||OwnedWindows.Cast<Window>().Any(w=>w.IsVisible);
    private void PollPointer()
    {
        if(handle==IntPtr.Zero||!IsVisible)return;
        UpdateInputTransparency();var p=NativeWindow.Cursor();var local=PointFromScreen(new Point(p.X,p.Y));var b=Island.ShellBounds;
        // Keep the departing silhouette available for reentry until the reverse morph settles.
        // The fixed transparent host and shadows never count as pointer presence.
        if(exitBounds is {} departing)
        {
            if(!animating)exitBounds=null;
            else if(!Suspended&&Near(local,departing,exitState))
            {
                var restore=exitState;exitBounds=null;interaction.Set(restore);ApplyState();return;
            }
        }
        var previous=State;
        if(interaction.Pointer(Near(local,b,State),clock.Elapsed,Suspended))
        {
            if(State==IslandState.Minimal){exitBounds=b;exitState=previous;}
            ApplyState();
        }
    }
    internal static bool Near(Point p,Rect bounds,IslandState state)
    {
        var horizontal=state==IslandState.Minimal?20:12;
        var bottom=state==IslandState.Minimal?18:16;
        return p.X>=bounds.Left-horizontal&&p.X<=bounds.Right+horizontal&&p.Y>=bounds.Top-20&&p.Y<=bounds.Bottom+bottom;
    }
    internal bool ShellContains(Point point)=>Island.Outline.FillContains(point);
    private void DisplayChanged(object? sender,EventArgs e)=>Dispatcher.BeginInvoke(RefreshGeometry);
    private void RefreshGeometry(){StopMotion();detailsReady=false;ConfigureHost();ApplyState(false);}
    private (double Width,double Height) Available(){var work=NativeWindow.WorkArea(handle);var scale=NativeWindow.Scale(handle);return (Math.Max(1,(work.Right-work.Left)/scale-16),Math.Max(1,(work.Bottom-work.Top)/scale-24));}
    private void ConfigureHost()
    {
        var available=Available();Width=Math.Min(ExpandedWidth,available.Width);Height=Math.Min(available.Height,480);
        Island.PrepareShadow(NativeWindow.Scale(handle));
        Details.Width=Math.Max(1,Width-58);Compact.Width=Math.Min(460,Math.Max(1,Width-60));
        AlertStrip.Update(Vm.MinimalAlerts,Now,false);PositionFrame();
    }
    public void Reposition(){if(handle==IntPtr.Zero)return;PositionFrame();}
    private double TargetWidth()=>State switch{IslandState.Minimal=>Vm.MinimalAlerts.Count>0?AlertStrip.CapsuleWidth:MinimalWidth,IslandState.Collapsed=>CollapsedWidth,_=>ExpandedWidth};
    private void PositionFrame(){nativePositions++;NativeWindow.Position(handle,Width,Height,settings.TopCenter,settings.Left,settings.Top,0);}
    private double ExpandedHeight()
    {
        if(detailsReady)return expandedHeight;
        Details.LayoutTransform=Transform.Identity;Details.Measure(new Size(Math.Min(ExpandedWidth,Width)-26,double.PositiveInfinity));
        var natural=Details.DesiredSize.Height+26;var max=Available().Height;
        if(natural>max){Details.LayoutTransform=new ScaleTransform(Math.Max(.5,(max-26)/Details.DesiredSize.Height),Math.Max(.5,(max-26)/Details.DesiredSize.Height));natural=max;}
        expandedHeight=natural;detailsReady=true;
        if(Height<natural+8){Height=Math.Min(max,natural+8);PositionFrame();}
        return natural;
    }
    public void SetState(IslandState state,bool animate=true){exitBounds=null;interaction.Set(state);ApplyState(animate);}
    public void SetExpanded(bool value,bool animate=true)=>SetState(value?IslandState.Expanded:IslandState.Collapsed,animate);
    private void StartFrames(){if(animating)return;animating=true;lastRenderingTime=TimeSpan.Zero;CompositionTarget.Rendering+=MotionFrame;}
    private void StopMotion(){CompositionTarget.Rendering-=MotionFrame;animating=false;}
    private IntPtr InputHook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message!=0x84||!IsVisible)return IntPtr.Zero;
        var packed=lParam.ToInt64();var p=PointFromScreen(new Point((short)(packed&0xffff),(short)((packed>>16)&0xffff)));
        if(!Island.Outline.FillContains(p)){handled=true;return new IntPtr(-1);}return IntPtr.Zero;
    }
    private void UpdateInputTransparency()
    {
        if(handle==IntPtr.Zero||!IsVisible)return;
        var p=NativeWindow.Cursor();var local=PointFromScreen(new Point(p.X,p.Y));
        NativeWindow.SetInputTransparent(handle,fullscreenHidden||!Island.Outline.FillContains(local));
    }
    private void PaintShell()
    {
        var a=expansion.Value;var w=widthMotion.Value-24*a;var h=heightMotion.Value-24*a;
        // One fixed center in physical space; no integer rounding of either moving edge.
        Island.Pose(new Rect((Width-w)/2,20*a,w,Math.Max(1,h)),a,alertMotion.Value*(1-a));
        ContentSurface.SetRenderClip(Island.ContentClip);
        paintedWidth=widthMotion.Value;paintedHeight=heightMotion.Value;
        compactTransform.X=0;compactTransform.Y=20*a+1;
        detailsTransform.Y=20*a+1;
        var reveal=Math.Clamp((heightMotion.Value-35)/23,0,1);
        Compact.Opacity=reveal;Compact.IsHitTestVisible=!IsExpanded&&reveal>.8;
        Minimal.Opacity=(1-a)*(1-alertMotion.Value);
        AlertStrip.Opacity=(1-a)*alertMotion.Value;
    }
    private void MotionFrame(object? sender,EventArgs e)
    {
        if(!animating||updatingFrame||VerificationCapturing)return;
        var render=((RenderingEventArgs)e).RenderingTime;if(render==lastRenderingTime)return;
        var frameMs=lastRenderingTime==TimeSpan.Zero?0:(render-lastRenderingTime).TotalMilliseconds;lastRenderingTime=render;
        updatingFrame=true;var began=Stopwatch.GetTimestamp();
        try
        {
            var now=Now;widthMotion.Sample(now);heightMotion.Sample(now);expansion.Sample(now);alertMotion.Sample(now);AlertStrip.Frame(now);PaintShell();UpdateInputTransparency();
            if(RecordMotion)MotionSamples.Add(new(now*1000,frameMs,Stopwatch.GetElapsedTime(began).TotalMilliseconds,VisualWidth,VisualHeight,expansion.Value,alertMotion.Value,Island.ShellBounds.X,Island.ShellBounds.Y,layouts,nativePositions));
            if(!widthMotion.Active&&!heightMotion.Active&&!expansion.Active&&!alertMotion.Active&&!AlertStrip.Active)StopMotion();
        }
        finally{updatingFrame=false;}
    }
    private void ApplyState(bool animate=true)
    {
        if(handle==IntPtr.Zero)return;
        HideTip();var minimal=State==IslandState.Minimal;var expanded=IsExpanded;var alert=Vm.MinimalAlerts.Count>0;
        if(expanded&&appliedState!=IslandState.Expanded)detailsReady=false;appliedState=State;
        Compact.Visibility=expanded?Visibility.Collapsed:Visibility.Visible;Details.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;
        Minimal.Visibility=Visibility.Visible;
        var w=Math.Min(TargetWidth(),Available().Width);var h=minimal?alert?AlertHeight:MinimalHeight:expanded?ExpandedHeight():CollapsedHeight;
        if(!expanded)Vm.HoverLane=null;
        var now=Now;animate&=SystemParameters.ClientAreaAnimation;var seconds=expanded?.28:.32;
        if(!animate)
        {
            StopMotion();widthMotion.Set(w);heightMotion.Set(h);expansion.Set(minimal?0:1);alertMotion.Set(alert?1:0);AlertStrip.Update(Vm.MinimalAlerts,now,false);
            Details.BeginAnimation(OpacityProperty,null);Details.Opacity=1;Heatmap.BeginAnimation(UsageHeatmap.RevealProperty,null);Heatmap.Reveal=1;PaintShell();UpdateInputTransparency();return;
        }
        widthMotion.Retarget(w,now,seconds);heightMotion.Retarget(h,now,seconds);expansion.Retarget(minimal?0:1,now,seconds);alertMotion.Retarget(alert?1:0,now,seconds);
        if(expanded)
        {
            Details.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(200)){BeginTime=TimeSpan.FromMilliseconds(40)});
            Heatmap.BeginAnimation(UsageHeatmap.RevealProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(620)){BeginTime=TimeSpan.FromMilliseconds(90)});
        }
        else {Heatmap.BeginAnimation(UsageHeatmap.RevealProperty,null);Heatmap.Reveal=1;}
        StartFrames();
    }
    public void HoverForSmoke(){interaction.Pointer(true,clock.Elapsed);if(interaction.Pointer(true,clock.Elapsed+Core.Models.IslandInteraction.ApproachDelay))ApplyState();}
    public void ClickForSmoke(){if(interaction.Click())ApplyState();}
    public void LeaveForSmoke(){interaction.Pointer(false,clock.Elapsed);if(interaction.Pointer(false,clock.Elapsed+(IsExpanded?Core.Models.IslandInteraction.ExpandedLeaveDelay:Core.Models.IslandInteraction.LeaveDelay)))ApplyState();}
    public Core.Models.GridDay? HoverDayForSmoke(DateOnly date)=>Heatmap.HoverDate(date);
    public bool TipVisible=>TipCard.Visibility==Visibility.Visible;
    public int ScrollViewersInPanel=>Count<ScrollViewer>(ContentSurface);
    private static int Count<T>(DependencyObject root) where T:DependencyObject{var n=root is T?1:0;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)n+=Count<T>(VisualTreeHelper.GetChild(root,i));return n;}
    private void OnEnter(object sender,MouseEventArgs e)=>PollPointer();
    private void OnLeave(object sender,MouseEventArgs e){HideTip();PollPointer();}
    private void OnKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Escape){exitBounds=null;interaction.Escape();ApplyState();}}
    private void HeaderDown(object sender,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is not DependencyObject source||FindParent<ButtonBase>(source) is not null)return;
        if(State!=IslandState.Collapsed&&!(IsExpanded&&FindParent<Grid>(source)==Strip||IsExpanded&&Strip.IsMouseOver))return;
        var point=e.GetPosition(this);if(!ShellContains(point))return;
        down=point;dragged=false;CaptureMouse();
    }
    private void HeaderCaptureLost(object sender,MouseEventArgs e){if(!dragging)down=null;}
    private void HeaderMove(object sender,MouseEventArgs e)
    {
        if(down is not {} point||e.LeftButton!=MouseButtonState.Pressed)return;
        var current=e.GetPosition(this);if(Math.Abs(current.X-point.X)+Math.Abs(current.Y-point.Y)<6)return;
        dragged=true;down=null;
        dragging=true;ReleaseMouseCapture();
        try{DragMove();settings.TopCenter=false;NativeWindow.GetWindowRect(handle,out var rect);var scale=NativeWindow.Scale(handle);settings.Left=rect.Left+(rect.Right-rect.Left-CollapsedWidth*scale)/2;settings.Top=rect.Top;Reposition();save();}catch(InvalidOperationException){}finally{dragging=false;}
    }
    private void HeaderUp(object sender,MouseButtonEventArgs e)
    {
        if(down is null)return;
        var click=!dragged&&ShellContains(e.GetPosition(this));down=null;ReleaseMouseCapture();
        if(click&&interaction.Click()){exitBounds=null;ApplyState();e.Handled=true;}
    }
    private static T? FindParent<T>(DependencyObject? node) where T:DependencyObject{while(node is not null){if(node is T match)return match;node=node is Visual or System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);}return null;}
    private void CenterClick(object sender,RoutedEventArgs e){settings.TopCenter=true;Reposition();save();}
    private void HideClick(object sender,RoutedEventArgs e){interaction.Escape();ApplyState(false);Hide();}
    private void HeatmapHovered(object? sender,HeatmapHoverEventArgs e)
    {
        Vm.ShowTip(e.Day);
        if(e.Day is null||!IsExpanded){HideTip();return;}
        TipCard.Visibility=Visibility.Visible;
        TipCard.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        var size=TipCard.DesiredSize;
        var tile=Heatmap.TransformToVisual(Overlay).TransformBounds(e.Tile);
        var x=Math.Clamp(tile.Left+tile.Width/2-size.Width/2,10,Math.Max(10,Overlay.ActualWidth-size.Width-10));
        var y=tile.Top-size.Height-6;if(y<10)y=tile.Bottom+6;
        Canvas.SetLeft(TipCard,x);Canvas.SetTop(TipCard,y);
    }
    private void HideTip(){TipCard.Visibility=Visibility.Collapsed;}
    private void LaneEnter(object sender,MouseEventArgs e){if(sender is FrameworkElement{Tag:string id})Vm.HoverLane=id;}
    private void LaneLeave(object sender,MouseEventArgs e){Vm.HoverLane=null;}
    private void LaneClick(object sender,MouseButtonEventArgs e){if(sender is FrameworkElement{Tag:string id})Vm.TogglePinnedLane(id);e.Handled=true;}
}

