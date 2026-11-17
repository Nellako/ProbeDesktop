using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ProbeDesktop.App.Views;

/// <summary>
/// 附加属性：内部列表已滚到边缘（或无需滚动）时，把鼠标滚轮交给外层页面，避免嵌套表格“卡住”整页滚动。
/// 用法：views:ScrollForwarding.Enabled="True"
/// </summary>
public static class ScrollForwarding
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ScrollForwarding), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject source)
        {
            return;
        }

        var inner = FindChild<ScrollViewer>(source);
        if (inner is not null)
        {
            var atTop = inner.VerticalOffset <= 0;
            var atBottom = inner.VerticalOffset >= inner.ScrollableHeight;
            var canScroll = inner.ScrollableHeight > 0 && ((e.Delta > 0 && !atTop) || (e.Delta < 0 && !atBottom));
            if (canScroll)
            {
                return;
            }
        }

        var parent = VisualTreeHelper.GetParent(source) as UIElement;
        if (parent is null)
        {
            return;
        }

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var deeper = FindChild<T>(child);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }
}
