using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinNetControl.Models;

public partial class AppSettings : ObservableObject
{
    private bool _enableGlobalSpeedWidget;
    public bool EnableGlobalSpeedWidget
    {
        get => _enableGlobalSpeedWidget;
        set => SetProperty(ref _enableGlobalSpeedWidget, value);
    }
    
    private double _widgetTransparency = 0.8;
    public double WidgetTransparency
    {
        get => _widgetTransparency;
        set => SetProperty(ref _widgetTransparency, value);
    }
    
    private int _widgetCornerRadius = 8;
    public int WidgetCornerRadius
    {
        get => _widgetCornerRadius;
        set => SetProperty(ref _widgetCornerRadius, value);
    }
    
    private bool _areDependenciesInstalled;
    public bool AreDependenciesInstalled
    {
        get => _areDependenciesInstalled;
        set => SetProperty(ref _areDependenciesInstalled, value);
    }
}
