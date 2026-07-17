using Microsoft.UI.Xaml;
using WinUIEx;
using System.Runtime.InteropServices;
using System;
using WinNetControl.Core;

namespace WinNetControl.Views;

public sealed partial class FloatingSpeedWidget : WindowEx
{
    public FloatingSpeedWidget()
    {
        this.InitializeComponent();
        
        // Remove border and title bar
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(null);
        
        // Make it always on top and click-through
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        
        // Set always on top
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
        
        // Make click-through (layered and transparent)
        int initialStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, initialStyle | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT);

        // Apply visual transparency backdrop (Mica/Acrylic handled in XAML ideally, or here)
        this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
    }
}
