using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace WinNetControl.Core;

/// <summary>
/// System-tray icon with live speed tooltip.
/// Uses Win32 Shell_NotifyIcon — no external library required.
/// </summary>
public sealed class TrayService : IDisposable
{
    // ── Win32 P/Invoke ────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint    cbSize;
        public nint    hWnd;
        public uint    uID;
        public uint    uFlags;
        public uint    uCallbackMessage;
        public nint    hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string  szTip;
        public uint    dwState;
        public uint    dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string  szInfo;
        public uint    uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string  szInfoTitle;
        public uint    dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);
    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string? lpModuleName);

    private const uint NIM_ADD    = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON    = 0x02;
    private const uint NIF_TIP     = 0x04;
    private const uint IDI_APP     = 32512; // default app icon

    // ── Fields ────────────────────────────────────────────────────────────────
    private NOTIFYICONDATA _nid;
    private bool           _added;
    private readonly nint  _hWnd;
    private readonly Timer _updateTimer;
    private readonly DispatcherQueue _dq;
    private Func<string>?  _tooltipProvider;


    public TrayService(nint mainHwnd, DispatcherQueue dq)
    {
        _hWnd = mainHwnd;
        _dq   = dq;

        nint hIcon = LoadIcon(GetModuleHandle(null), (nint)IDI_APP);

        _nid = new NOTIFYICONDATA
        {
            cbSize          = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd            = mainHwnd,
            uID             = 1,
            uFlags          = NIF_ICON | NIF_TIP,
            hIcon           = hIcon,
            szTip           = "WinNetControl",
            szInfo          = "",
            szInfoTitle     = "",
            uCallbackMessage = 0,
            uTimeoutOrVersion = 0,
            dwState         = 0,
            dwStateMask     = 0,
        };

        Shell_NotifyIcon(NIM_ADD, ref _nid);
        _added = true;

        // Update tooltip every 2 s
        _updateTimer = new Timer(_ => UpdateTooltip(), null, 2000, 2000);
    }

    public void SetTooltipProvider(Func<string> provider)
        => _tooltipProvider = provider;

    private void UpdateTooltip()
    {
        if (!_added) return;
        try
        {
            string tip = _tooltipProvider?.Invoke() ?? "WinNetControl";
            // Tooltip max 127 chars
            if (tip.Length > 127) tip = tip[..127];
            _nid.szTip = tip;
            _nid.uFlags = NIF_TIP;
            Shell_NotifyIcon(NIM_MODIFY, ref _nid);
        }
        catch { }
    }

    public void Dispose()
    {
        _updateTimer.Dispose();
        if (_added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _added = false;
        }
    }
}
