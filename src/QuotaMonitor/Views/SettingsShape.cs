using System.Windows;

namespace QuotaMonitor.Views;

public static class AppVersion
{
    public static string Display { get; } = "v" + typeof(AppVersion).Assembly.GetName().Version!.ToString(3);
}

// The outer segmented container has r12 and a 4 DIP inset; its inner surface is r8.
public static class SettingsShape
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(SettingsShape), new PropertyMetadata(new CornerRadius(12)));
    public static CornerRadius GetRadius(DependencyObject element) => (CornerRadius)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, CornerRadius value) => element.SetValue(RadiusProperty, value);
}
