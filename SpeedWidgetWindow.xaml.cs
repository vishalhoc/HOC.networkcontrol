using System;
using Microsoft.UI.Xaml;
using WinUIEx;
using WinNetControl.Models;
using WinNetControl.ViewModels;
using System.ComponentModel;

namespace WinNetControl;

public sealed partial class SpeedWidgetWindow : WindowEx, INotifyPropertyChanged
{
    private readonly ProcessNetworkInfo? _targetProcess;
    private DispatcherTimer _timer;
    private AppConfig _config;

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _uploadText = "U: 0.0 KB/s";
    public string UploadText
    {
        get => _uploadText;
        set
        {
            _uploadText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UploadText)));
        }
    }

    private string _downloadText = "D: 0.0 KB/s";
    public string DownloadText
    {
        get => _downloadText;
        set
        {
            _downloadText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadText)));
        }
    }

    public Microsoft.UI.Xaml.Media.Brush BackgroundBrush => 
        _config.WidgetDisableTransparency 
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) 
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(1, 0, 0, 0));

    public Microsoft.UI.Xaml.Controls.Orientation LayoutOrientation => 
        _config.WidgetLayout == "Horizontal" ? Microsoft.UI.Xaml.Controls.Orientation.Horizontal : Microsoft.UI.Xaml.Controls.Orientation.Vertical;

    public double WidgetFontSize => _config.WidgetFontSize;

    private MainViewModel? _globalViewModel;

    public SpeedWidgetWindow(ProcessNetworkInfo? targetProcess = null, MainViewModel? globalViewModel = null)
    {
        _config = WinNetControl.Core.ConfigService.Load();

        // MUST assign before InitializeComponent so x:Bind compiled bindings can read them
        _targetProcess   = targetProcess;
        _globalViewModel = globalViewModel;

        this.InitializeComponent();

        SettingsWindow.WidgetSettingsChanged += OnGlobalWidgetSettingsChanged;

        SetWindowProperties();

        _timer = new DispatcherTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(_config.WidgetRefreshRateMs);
        _timer.Tick += (s, e) =>
        {
            double up = 0, down = 0;
            if (_targetProcess != null)
            {
                up = _targetProcess.UploadSpeed;
                down = _targetProcess.DownloadSpeed;
            }
            else if (_globalViewModel != null)
            {
                up = _globalViewModel.GlobalUploadSpeed;
                down = _globalViewModel.GlobalDownloadSpeed;
            }
            UploadText   = $"↑ {FormatSpeed(up)}";
            DownloadText = $"↓ {FormatSpeed(down)}";
        };
        _timer.Start();
        
        this.Closed += Window_Closed;
    }


    private void SetWindowProperties()
    {
        this.IsAlwaysOnTop = true;
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(RootGrid);

        // Ensure reasonable minimums so speeds are not cut off
        double minWidth = _config.WidgetLayout == "Horizontal" ? 180 : 120;
        double minHeight = _config.WidgetLayout == "Horizontal" ? 40 : 65;

        this.Width = Math.Max(_config.WidgetWidth, minWidth);
        this.Height = Math.Max(_config.WidgetHeight, minHeight);

        byte opacityValue = (byte)((_config.WidgetOpacity / 100.0) * 255);
        WinUIEx.HwndExtensions.SetWindowOpacity(this.GetWindowHandle(), opacityValue);
    }

    private void OnGlobalWidgetSettingsChanged(object? sender, EventArgs? e)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            _config = WinNetControl.Core.ConfigService.Load();
            _timer.Interval = TimeSpan.FromMilliseconds(_config.WidgetRefreshRateMs);
            
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LayoutOrientation)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetFontSize)));
            
            SetWindowProperties();
        });
    }

    private static string FormatSpeed(double kbps)
    {
        if (kbps >= 1024)
            return $"{kbps / 1024.0:F1} MB/s";
        return $"{kbps:F1} KB/s";
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        SettingsWindow.WidgetSettingsChanged -= OnGlobalWidgetSettingsChanged;
    }
}
