using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ProbeDesktop.App.ViewModels;
using ProbeDesktop.Core;

namespace ProbeDesktop.App.Converters;

/// <summary>true → Visible；Invert=true 时取反。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        return flag ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null、空字符串或 0 → Collapsed；其他 → Visible。Invert=true 时取反。</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            _ => true,
        };
        return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>DateTimeOffset → 本地时间文本，ConverterParameter 为格式串。</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public static readonly LocalTimeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset t => t.ToLocalTime().ToString(parameter as string ?? "yyyy-MM-dd HH:mm", culture),
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>整条 ConnectionTestResult → 结果文字（成功 / 成功（可能）/ 失败 / 结果未知，中间阶段沿用阶段名）。</summary>
public sealed class ConnectionResultTextConverter : IValueConverter
{
    public static readonly ConnectionResultTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ConnectionTestResult r ? ConnectionHistoryViewModel.ResultLabel(r) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>整条 ConnectionTestResult → 色调键：ok / possible / bad / warn / none。</summary>
public sealed class ConnectionToneConverter : IValueConverter
{
    public static readonly ConnectionToneConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ConnectionTestResult r
            ? ProbeDesktop.App.Logic.LatestResultPicker.OutcomeTone(ProbeDesktop.App.Logic.LatestResultPicker.Classify(r))
            : "none";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>ConnectionContextCheck → 色调键：ok / warn / none。</summary>
public sealed class ConnectionContextToneConverter : IValueConverter
{
    public static readonly ConnectionContextToneConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ConnectionContextCheck c
            ? ProbeDesktop.App.Services.ProtocolInfo.ContextTone(c)
            : "none";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>连接记录、巡检状态、协议枚举 → 中文展示文本。</summary>
public sealed class EnumTextConverter : IValueConverter
{
    public static readonly EnumTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConnectionRecordSource s => ConnectionHistoryViewModel.SourceText(s),
        ConnectionContextCheck s => ProbeDesktop.App.Services.ProtocolInfo.ContextLabel(s),
        ConnectionTestStage s => ConnectionHistoryViewModel.StageText(s),
        ApiProtocol.ChatCompletions => "Chat Completions",
        ApiProtocol.OpenAIResponses => "Responses",
        ApiProtocol.AnthropicMessages => "Messages",
        InspectionAuthorizationState.PendingApproval => "待确认",
        InspectionAuthorizationState.Authorized => "已授权",
        InspectionAuthorizationState.Paused => "已暂停",
        InspectionAuthorizationState.NoCachedModels => "无缓存模型",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
