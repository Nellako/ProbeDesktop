using System.Windows;
using System.Windows.Media;

namespace ProbeDesktop.App.Themes;

/// <summary>侧边导航项的展示附加属性：分组标题与线性图标。仅用于外观。</summary>
public static class NavAssist
{
    public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
        "Group", typeof(string), typeof(NavAssist), new PropertyMetadata(null));

    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(NavAssist), new PropertyMetadata(null));

    public static string? GetGroup(DependencyObject d) => (string?)d.GetValue(GroupProperty);

    public static void SetGroup(DependencyObject d, string? value) => d.SetValue(GroupProperty, value);

    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);

    public static void SetIcon(DependencyObject d, Geometry? value) => d.SetValue(IconProperty, value);
}
