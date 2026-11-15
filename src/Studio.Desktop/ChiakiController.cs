using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Studio.Desktop;

internal sealed class ChiakiController
{
    // Default keyboard layout in chiaki-ng 1.10.1. Stick directions use full deflection.
    internal static readonly IReadOnlyDictionary<string,int> Keys = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Up"]=0x26, ["Down"]=0x28, ["Left"]=0x25, ["Right"]=0x27,
        ["Cross"]=0x0D, ["Circle"]=0x08, ["Square"]=0xDC, ["Triangle"]='C',
        ["L1"]='2', ["L2"]='1', ["L3"]='5', ["R1"]='3', ["R2"]='4', ["R3"]='6',
        ["Options"]='O', ["Share"]='F', ["Touchpad"]='T', ["PS"]=0x1B,
        ["LeftStickLeft"]=0xDB, ["LeftStickRight"]=0xDD, ["LeftStickUp"]=0x2D, ["LeftStickDown"]=0x2E,
        ["RightStickLeft"]=0xBD, ["RightStickRight"]=0xBB, ["RightStickUp"]=0x21, ["RightStickDown"]=0x22
    };

    [DllImport("user32")] static extern bool SetForegroundWindow(nint h);
    [DllImport("user32")] static extern nint GetForegroundWindow();
    [DllImport("user32")] static extern bool IsIconic(nint h);
    [DllImport("user32")] static extern uint MapVirtualKeyW(uint key, uint type);
    [DllImport("user32", SetLastError=true)] static extern bool PostMessageW(nint h,uint msg,nuint key,nint detail);
    [DllImport("user32")] static extern bool GetClientRect(nint h,out Rect rect);
    [DllImport("user32")] static extern bool PrintWindow(nint h,nint dc,uint flags);
    [DllImport("user32")] static extern nint GetDC(nint h);
    [DllImport("user32")] static extern int ReleaseDC(nint h,nint dc);
    [DllImport("gdi32")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] static extern nint CreateCompatibleBitmap(nint dc,int width,int height);
    [DllImport("gdi32")] static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] static extern bool DeleteDC(nint dc);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }

    private readonly nint window;
    private ChiakiController(nint window) { this.window=window; }
    internal static async Task<ChiakiController> ConnectAsync(CancellationToken ct)
    {
        var windows=new List<nint>();
        foreach(var p in Process.GetProcessesByName("chiaki"))
        {
            using(p) { if(p.MainWindowHandle!=0) windows.Add(p.MainWindowHandle); }
        }
        if(windows.Count!=1 || IsIconic(windows[0]))
            throw new InvalidOperationException("Open one connected chiaki-ng window and restore it.");
        var controller=new ChiakiController(windows[0]);
        SetForegroundWindow(controller.window);
        await Task.Delay(200,ct);
        controller.CheckFocus();
        return controller;
    }

    internal void CheckFocus()
    {
        if(GetForegroundWindow()!=window) throw new InvalidOperationException("Stopped because chiaki-ng lost focus.");
    }

    private void Key(int key,bool down)
    {
        var scan=MapVirtualKeyW((uint)key,4);
        int detail=1 | (int)(scan&255)<<16;
        if((scan>>8)==0xE0) detail|=1<<24;
        if(!down) detail|=unchecked((int)0xC0000000);
        if(!PostMessageW(window,down?0x100u:0x101u,(nuint)key,detail)) throw new Win32Exception();
    }

    internal async Task PressAsync(string[] names,int milliseconds,CancellationToken ct)
    {
        if(names.Length>4 || milliseconds is < 40 or > 2000)
            throw new ArgumentException("Use at most four buttons for 40–2000 ms.");
        var keys=names.Select(n=>Keys.TryGetValue(n,out var k)?k:throw new ArgumentException("Unknown control: "+n)).Distinct().ToArray();
        CheckFocus();
        var held=new Stack<int>();
        try
        {
            foreach(var key in keys) { ct.ThrowIfCancellationRequested(); Key(key,true); held.Push(key); }
            var timer=Stopwatch.StartNew();
            while(timer.ElapsedMilliseconds<milliseconds)
            {
                await Task.Delay((int)Math.Clamp(milliseconds-timer.ElapsedMilliseconds,1,25),ct);
                CheckFocus();
            }
        }
        finally
        {
            Exception? failure=null;
            while(held.TryPop(out var key)) try { Key(key,false); } catch(Win32Exception ex){ failure=ex; }
            if(failure!=null) throw failure;
        }
    }

    internal BitmapSource Capture()
    {
        CheckFocus();
        if(!GetClientRect(window,out var rect) || rect.Right<=0 || rect.Bottom<=0)
            throw new InvalidOperationException("The stream has no visible client area.");
        var screen=GetDC(window);
        var dc=CreateCompatibleDC(screen);
        var bitmap=CreateCompatibleBitmap(screen,rect.Right,rect.Bottom);
        var previous=SelectObject(dc,bitmap);
        try
        {
            if(bitmap==0 || dc==0 || !PrintWindow(window,dc,3))
                throw new InvalidOperationException("Unable to capture the chiaki-ng window.");
            var frame=Imaging.CreateBitmapSourceFromHBitmap(bitmap,0,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
            frame.Freeze();
            return frame;
        }
        finally { SelectObject(dc,previous); DeleteObject(bitmap); DeleteDC(dc); ReleaseDC(window,screen); }
    }
}
