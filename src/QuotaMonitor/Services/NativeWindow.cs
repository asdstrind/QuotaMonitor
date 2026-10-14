using System.Runtime.InteropServices;
using QuotaMonitor.Core.Models;
namespace QuotaMonitor.Services;
public static class NativeWindow
{
    [StructLayout(LayoutKind.Sequential)]public struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct MonitorInfo{public int Size;public Rect Monitor,Work;public uint Flags;}
    [DllImport("user32")]private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32")]private static extern IntPtr MonitorFromPoint(Point point,uint flags);
    [DllImport("user32")]private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32")]public static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32")]private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32")]private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr hwnd,System.Text.StringBuilder name,int capacity);
    [DllImport("user32")]public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32")]private static extern bool ShowWindow(IntPtr hwnd,int show);
    [DllImport("user32")]private static extern bool GetCursorPos(out Point point);
    public static (int X,int Y) Cursor(){GetCursorPos(out var point);return (point.X,point.Y);}
    public static void ShowWithoutActivating(IntPtr hwnd)=>ShowWindow(hwnd,4);
    [DllImport("user32",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32",EntryPoint="SetWindowLongPtrW")]private static extern IntPtr SetWindowLongPtr(IntPtr window,int index,IntPtr value);
    public static Rect WorkArea(IntPtr hwnd){var i=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};GetMonitorInfo(MonitorFromWindow(hwnd,2),ref i);return i.Work;}
    public static double Scale(IntPtr hwnd)=>Math.Max(1,GetDpiForWindow(hwnd)/96d);
    public static void ToolWindow(IntPtr hwnd){var style=GetWindowLongPtr(hwnd,-20).ToInt64();SetWindowLongPtr(hwnd,-20,new IntPtr((style|0x80)&~0x40000));}
    public static void VerificationInspectable(IntPtr hwnd){var style=GetWindowLongPtr(hwnd,-20).ToInt64();SetWindowLongPtr(hwnd,-20,new IntPtr(style&~0x80));}
    [DllImport("dwmapi")]private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    public static void DisableNonClientRendering(IntPtr hwnd){var disabled=1;DwmSetWindowAttribute(hwnd,2,ref disabled,sizeof(int));}
    public static void SetInputTransparent(IntPtr hwnd,bool transparent){var style=GetWindowLongPtr(hwnd,-20).ToInt64();var next=transparent?style|0x20:style&~0x20;if(next!=style)SetWindowLongPtr(hwnd,-20,new IntPtr(next));}
    public static bool IsTopmost(IntPtr hwnd)=>(GetWindowLongPtr(hwnd,-20).ToInt64()&8)!=0;
    public static void Position(IntPtr hwnd,double dipWidth,double dipHeight,bool center,double x,double y,double topInset=8){var monitor=center?MonitorFromWindow(hwnd,2):MonitorFromPoint(new Point{X=(int)x,Y=(int)y},2);var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};GetMonitorInfo(monitor,ref info);var a=info.Work;var r=IslandDock.Place(new(a.Left,a.Top,a.Right,a.Bottom),Scale(hwnd),dipWidth,dipHeight,center,x,topInset);SetWindowPos(hwnd,new IntPtr(-1),r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top,0x10);}
    public static bool IsFullscreen(IntPtr own){var foreground=GetForegroundWindow();if(foreground==IntPtr.Zero||foreground==own)return false;var name=new System.Text.StringBuilder(128);GetClassName(foreground,name,name.Capacity);if(name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd")return false;var i=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};GetMonitorInfo(MonitorFromWindow(foreground,2),ref i);if(!GetWindowRect(foreground,out var r))return false;return r.Left<=i.Monitor.Left&&r.Top<=i.Monitor.Top&&r.Right>=i.Monitor.Right&&r.Bottom>=i.Monitor.Bottom;}
}
