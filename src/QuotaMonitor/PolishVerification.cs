using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Services;
using QuotaMonitor.Views;

namespace QuotaMonitor;

public partial class App
{
    private static RenderTargetBitmap AlphaRender(Window window,double scale)
    {
        // RenderTargetBitmap can synchronously re-enter CompositionTarget.Rendering.
        // Keep pixels and their inspection contour on the exact same sampled pose.
        var islandWindow=window as IslandWindow;
        if(islandWindow is not null)islandWindow.VerificationCapturing=true;
        try
        {
        window.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*scale),(int)Math.Ceiling(window.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
        bitmap.Render(window);return bitmap;
        }
        finally{if(islandWindow is not null)islandWindow.VerificationCapturing=false;}
    }
    private static void WritePng(BitmapSource bitmap,string path)
    {
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
    private static (byte Center,byte Corner,int OpaqueOutside,int ShadowPixels,byte EdgeAlpha) InspectAlpha(Window window,RenderTargetBitmap bitmap,double scale)
    {
        var shell=(MorphSurface)window.FindName("Island");
        return InspectAlpha(bitmap,scale,new Point(shell.ShellBounds.X,shell.ShellBounds.Y),RoundedSilhouette.Create(shell.ShellBounds.Width,shell.ShellBounds.Height,shell.CornerRadius),shell.ShellBounds.Height);
    }
    private static (byte Center,byte Corner,int OpaqueOutside,int ShadowPixels,byte EdgeAlpha) InspectAlpha(RenderTargetBitmap bitmap,double scale,Point origin,Geometry geometry,double shellHeight)
    {
        var bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);
        byte Alpha(int x,int y)=>bytes[(Math.Clamp(y,0,bitmap.PixelHeight-1)*bitmap.PixelWidth+Math.Clamp(x,0,bitmap.PixelWidth-1))*4+3];
        var fringe=new Pen(Brushes.Black,2/scale);fringe.Freeze();
        var maskVisual=new DrawingVisual();
        using(var dc=maskVisual.RenderOpen()){dc.PushTransform(new TranslateTransform(origin.X,origin.Y));dc.DrawGeometry(Brushes.White,new Pen(Brushes.White,2/scale),geometry);dc.Pop();}
        var mask=new RenderTargetBitmap(bitmap.PixelWidth,bitmap.PixelHeight,96*scale,96*scale,PixelFormats.Pbgra32);mask.Render(maskVisual);
        var maskBytes=new byte[bytes.Length];mask.CopyPixels(maskBytes,bitmap.PixelWidth*4,0);
        var outside=0;var shadow=0;byte edgeAlpha=0;
        for(var y=0;y<bitmap.PixelHeight;y++)for(var x=0;x<bitmap.PixelWidth;x++)
        {
            // Exclude the one-pixel antialiasing fringe of the actual circular contour.
            if(maskBytes[(y*bitmap.PixelWidth+x)*4+3]>0)continue;
            var alpha=Alpha(x,y);if(alpha>160)outside++;if(alpha>0)shadow++;
            if(x==0||y==0||x==bitmap.PixelWidth-1||y==bitmap.PixelHeight-1)edgeAlpha=Math.Max(edgeAlpha,alpha);
        }
        return (Alpha(bitmap.PixelWidth/2,(int)((origin.Y+shellHeight/2)*scale)),
            new[]{Alpha(0,0),Alpha(bitmap.PixelWidth-1,0),Alpha(0,bitmap.PixelHeight-1),Alpha(bitmap.PixelWidth-1,bitmap.PixelHeight-1)}.Max(),outside,shadow,edgeAlpha);
    }
    private static async Task VerifyPolishAsync(IslandWindow window,string folder,Dictionary<string,bool> checks)
    {
        var sequence=Path.Combine(folder,"morph-sequence");Directory.CreateDirectory(sequence);
        var hwnd=new WindowInteropHelper(window).Handle;var dpi=NativeWindow.Scale(hwnd);
        window.VerificationSuspendsPointer=true;
        try
        {
            var frames=new List<object>();var paths=new List<string>();var centers=new List<int>();var widths=new List<double>();
            var opacity=true;var opaqueOutside=0;var ordinal=0;
            var elapsed=System.Diagnostics.Stopwatch.StartNew();
            var samples=new List<(string Name,string Phase,double Ms,object Pose,NativeWindow.Rect Bounds,RenderTargetBitmap Bitmap,Point Origin,Geometry Geometry,double ShellHeight)>();
            async Task Frame(string phase)
            {
                window.UpdateLayout();NativeWindow.GetWindowRect(hwnd,out var bounds);
                var bitmap=AlphaRender(window,dpi);bitmap.Freeze();
                var shell=(Border)window.FindName("Island");
                var name=$"{ordinal++:D3}-{phase}";
                DesktopCapture(window,Path.Combine(sequence,name+"-desktop.png"));
                centers.Add(bounds.Left+bounds.Right);widths.Add(window.Width);
                opacity&=shell.Opacity==1;
                samples.Add((name,phase,elapsed.Elapsed.TotalMilliseconds,window.MorphEvidence,bounds,bitmap,shell.TranslatePoint(new Point(),window),RoundedSilhouette.Create(shell.ActualWidth,shell.ActualHeight,shell.CornerRadius),shell.ActualHeight));
                await Task.Delay(20);
            }
            window.SetState(IslandState.Minimal,false);await Task.Delay(100);await Frame("minimal");
            window.SetState(IslandState.Collapsed);
            for(var i=0;i<14;i++)await Frame("grow");
            await Task.Delay(100);await Frame("collapsed");
            window.SetState(IslandState.Minimal);
            checks["ReverseStartsWithoutStripContentFlash"]=!SystemParameters.ClientAreaAnimation||((Border)window.FindName("Minimal")).Opacity==0;
            for(var i=0;i<14;i++)await Frame("shrink");
            await Task.Delay(100);await Frame("minimal-final");
            // Analyze and encode after recording, so validation work cannot stall the actual animation.
            foreach(var sample in samples)
            {
                var pixels=InspectAlpha(sample.Bitmap,dpi,sample.Origin,sample.Geometry,sample.ShellHeight);
                var path=Path.Combine(sequence,sample.Name+"-alpha.png");WritePng(sample.Bitmap,path);paths.Add(path);
                opacity&=pixels.Center>=250;opaqueOutside+=pixels.OpaqueOutside;
                frames.Add(new{Index=frames.Count,Phase=sample.Phase,ElapsedMilliseconds=sample.Ms,Pose=sample.Pose,Bounds=sample.Bounds,CenterAlpha=pixels.Center,OuterCornerAlpha=pixels.Corner,OpaqueOutside=pixels.OpaqueOutside,ShadowPixels=pixels.ShadowPixels,AlphaImage=Path.GetFileName(path),DesktopImage=sample.Name+"-desktop.png"});
            }
            checks["MorphHasIntermediateSilhouettes"]=!SystemParameters.ClientAreaAnimation||widths.Count(w=>w>180&&w<510)>=4;
            checks["MorphCenterAnchorStable"]=centers.Max()-centers.Min()<=2;
            checks["MorphShellNeverCrossfades"]=opacity;
            checks["MorphNoOpaquePixelsOutsideContour"]=opaqueOutside==0;
            checks["MorphFinalMinimalExact"]=window.State==IslandState.Minimal&&window.Width==176&&window.Height==8;
            await File.WriteAllTextAsync(Path.Combine(sequence,"frames.json"),JsonSerializer.Serialize(new{SystemAnimations=SystemParameters.ClientAreaAnimation,ActualDpiScale=dpi,Frames=frames},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
            ContactSheet(paths,Path.Combine(folder,"morph-contact-sheet.png"));

            var raster=new List<object>();
            foreach(var state in new[]{IslandState.Minimal,IslandState.Collapsed,IslandState.Expanded})
            {
                window.SetState(state,false);await Task.Delay(100);
                if(state==IslandState.Expanded)checks["ExpandedLayoutAndHeatmapRetained"]=window.ActualHeight>200&&((FrameworkElement)window.FindName("Heatmap")).ActualHeight>100;
                foreach(var scale in new[]{1d,1.25,1.5,2,3})
                {
                    var bitmap=AlphaRender(window,scale);var pixels=InspectAlpha(window,bitmap,scale);
                    WritePng(bitmap,Path.Combine(folder,$"{state.ToString().ToLowerInvariant()}-alpha-{scale*100:0}.png"));
                    raster.Add(new{State=state.ToString(),Scale=scale,pixels.Center,pixels.Corner,pixels.OpaqueOutside,pixels.ShadowPixels,pixels.EdgeAlpha});
                    checks[$"{state}TransparentContourAt{scale*100:0}Percent"]=pixels.OpaqueOutside==0&&pixels.Center==255;
                    if(state!=IslandState.Minimal)checks[$"{state}RetainsSoftShadowAt{scale*100:0}Percent"]=pixels.ShadowPixels>0;
                    if(state!=IslandState.Minimal)checks[$"{state}NoRectangularShadowCutoffAt{scale*100:0}Percent"]=pixels.EdgeAlpha==0;
                }
            }
            await File.WriteAllTextAsync(Path.Combine(folder,"alpha-dpi.json"),JsonSerializer.Serialize(raster,new JsonSerializerOptions{WriteIndented=true}));

            var jumps=false;var stressAnchors=new List<int>();
            for(var i=0;i<32;i++)
            {
                var width=window.Width;var height=window.Height;
                window.SetState(i%3==0?IslandState.Expanded:i%3==1?IslandState.Minimal:IslandState.Collapsed);
                jumps|=Math.Abs(width-window.Width)>.01||Math.Abs(height-window.Height)>.01;
                await Task.Delay(17+i%4*9);NativeWindow.GetWindowRect(hwnd,out var b);stressAnchors.Add(b.Left+b.Right);
            }
            window.SetState(IslandState.Minimal);await Task.Delay(650);
            checks["RapidRetargetNoGeometryJump"]=!jumps;
            checks["RapidRetargetCenterStable"]=stressAnchors.Max()-stressAnchors.Min()<=2;
            checks["RapidRetargetFinalStateNoStaleCompletion"]=window.State==IslandState.Minimal&&window.Width==176&&window.Height==8;

            // A real underlying HWND must receive clicks at transparent corners AND over the visible shadow.
            var clicks=0;var button=new Button{Content="Rounded corner input probe"};button.Click+=(_,_)=>clicks++;
            var probe=new Window{WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Content=button};
            try
            {
                probe.Show();NativeWindow.Position(new WindowInteropHelper(probe).Handle,800,200,true,0,0,0);
                foreach(var state in new[]{IslandState.Collapsed,IslandState.Expanded})
                {
                    window.SetState(state,false);await Task.Delay(120);NativeWindow.GetWindowRect(hwnd,out var bounds);
                    var before=clicks;
                    VerificationInput.Move(bounds.Left+(int)(13*dpi),bounds.Top+(int)(13*dpi));await Task.Delay(80);VerificationInput.Click();await Task.Delay(80);
                    VerificationInput.Move(bounds.Left+(int)(7*dpi),bounds.Top+(int)(40*dpi));await Task.Delay(80);VerificationInput.Click();await Task.Delay(80);
                    checks[$"{state}CornerAndShadowClicksPassThrough"]=clicks==before+2;
                }
            }
            finally{probe.Close();}
            window.SetState(IslandState.Expanded,false);
        }
        finally{window.VerificationSuspendsPointer=false;}
    }
    private static void ContactSheet(List<string> paths,string output)
    {
        const int cellWidth=860,cellHeight=190;var indices=new[]{0,2,3,4,6,15,16,17,18,20,22,30}.Where(i=>i<paths.Count).ToArray();
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(paths[0])!,"frames.json")));
        var frames=manifest.RootElement.GetProperty("Frames").EnumerateArray().ToArray();
        var anchorTop=frames[0].GetProperty("Bounds").GetProperty("Top").GetInt32();
        using var sheet=new System.Drawing.Bitmap(cellWidth*2,cellHeight*((indices.Length+1)/2));
        using var g=System.Drawing.Graphics.FromImage(sheet);g.Clear(System.Drawing.Color.FromArgb(233,236,240));
        using var font=new System.Drawing.Font("Segoe UI",11);using var ink=new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(42,49,60));
        using var lighter=new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(247,248,250));
        for(var k=0;k<indices.Length;k++)
        {
            var ox=k%2*cellWidth;var oy=k/2*cellHeight;
            for(var y=0;y<cellHeight;y+=12)for(var x=0;x<cellWidth;x+=12)if((x/12+y/12)%2==0)g.FillRectangle(lighter,ox+x,oy+y,12,12);
            using var image=System.Drawing.Image.FromFile(paths[indices[k]]);
            var frame=frames[indices[k]];var pose=frame.GetProperty("Pose");
            g.DrawString($"{indices[k]:D3} {frame.GetProperty("Phase").GetString()} | {frame.GetProperty("ElapsedMilliseconds").GetDouble():0} ms | {pose.GetProperty("Width").GetDouble():0.0} x {pose.GetProperty("Height").GetDouble():0.0} DIP",font,ink,ox+16,oy+10);
            g.DrawImageUnscaled(image,ox+(cellWidth-image.Width)/2,oy+45+frame.GetProperty("Bounds").GetProperty("Top").GetInt32()-anchorTop);
        }
        sheet.Save(output,System.Drawing.Imaging.ImageFormat.Png);
    }
}
