using System.Windows;
using System.Windows.Controls;
using ProbeDesktop.App.Services;
using ProbeDesktop.App.ViewModels;

namespace ProbeDesktop.App.Views;

public partial class ProfilesView : UserControl
{
    private ProfilesViewModel? _vm;

    public ProfilesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ProfilesViewModel);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Attach(DataContext as ProfilesViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 离开页面时清空未保存的 Key 输入。
        KeyBox.Clear();
        Attach(null);
    }

    private void Attach(ProfilesViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.ClearKeyRequested -= OnClearKeyRequested;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.ClearKeyRequested += OnClearKeyRequested;
        }
    }

    private void OnClearKeyRequested(object? sender, EventArgs e) => KeyBox.Clear();

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.IsSaving || !_vm.CanSave)
        {
            return;
        }

        try
        {
            // 先校验其它字段；校验失败时不读取、不清空 Key，避免用户重输。
            if (!_vm.ValidateEditor(KeyBox.SecurePassword.Length > 0))
            {
                return;
            }

            // 读取后立即清空控件。字符串仅作为参数传入 Core，不写入日志或任何绑定属性。
            string? key = KeyBox.Password;
            KeyBox.Clear();
            if (string.IsNullOrWhiteSpace(key))
            {
                key = null;
            }

            await _vm.SaveAsync(key);
            key = null;
        }
        catch (Exception ex)
        {
            KeyBox.Clear();
            UiErrors.Report(ex);
        }
    }
}
