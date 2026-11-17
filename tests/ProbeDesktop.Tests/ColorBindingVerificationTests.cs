using System.Xml.Linq;

namespace ProbeDesktop.Tests;

/// <summary>XAML resource wiring checks, not a substitute for rendering the native WPF window.</summary>
public static class ColorBindingVerificationTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly (string File, string Border, string Text, string Binding)[] MarkerStyles =
    [
        ("Views/InspectionView.xaml", "ContextBadgeBorder", "ContextBadgeText", "ContextTone"),
        ("Views/ProbeView.xaml", "RecentContextBorder", "RecentContextText", "ContextTone"),
        ("Windows/ConnectionHistoryWindow.xaml", "ContextBadgeBorder", "ContextBadgeText", "ConnectionContextToneConverter.Instance"),
    ];

    public static Task MarkerStylesUseRealGreenBlueRedBrushes()
    {
        var (theme, pages) = Load();
        foreach (var (file, border, text, binding) in MarkerStyles)
            VerifyMarkerStyles(pages[file], theme, border, text, binding, file);
        return Task.CompletedTask;
    }

    public static Task ConnectionStylesUseRealGreenBlueRedBrushes()
    {
        var (theme, pages) = Load();
        foreach (var (file, style, binding) in new[]
        {
            ("Views/InspectionView.xaml", "ResultTone", "Tone"),
            ("Views/ProbeView.xaml", "RecentTone", "Tone"),
            ("Windows/ConnectionHistoryWindow.xaml", "BadgeText", "ConnectionToneConverter.Instance"),
            ("Views/ConnectionTestCard.xaml", "ToneText", "Tone"),
        }) VerifyToneStyle(pages[file], theme, style, binding, "Foreground", "Brush", file);
        VerifyToneStyle(pages["Windows/ConnectionHistoryWindow.xaml"], theme, "BadgeBorder",
            "ConnectionToneConverter.Instance", "Background", "LightBrush", "history result background");
        return Task.CompletedTask;
    }

    public static Task GuardRejectsOldWarnOnlyAndWrongBrushMutations()
    {
        var (theme, pages) = Load();
        // Mutate XML documents in memory only: never touch production files or depend on token checks alone.
        foreach (var (file, border, text, binding) in MarkerStyles)
        {
            VerifyMarkerStyles(pages[file], theme, border, text, binding, file);
            var old = new XDocument(pages[file]);
            foreach (var key in new[] { border, text })
            {
                var triggers = Style(old, key).Descendants(Wpf + "DataTrigger").ToArray();
                foreach (var trigger in triggers.Where(t => (string?)t.Attribute("Value") is "possible" or "bad")) trigger.Remove();
            }
            ExpectRejected(() => VerifyMarkerStyles(old, theme, border, text, binding, file), file + ": old ok/warn-only style must fail");

            var wrong = new XDocument(pages[file]);
            var possible = Style(wrong, text).Descendants(Wpf + "DataTrigger").Single(t => (string?)t.Attribute("Value") == "possible");
            possible.Elements(Wpf + "Setter").Single(s => (string?)s.Attribute("Property") == "Foreground")
                .SetAttributeValue("Value", "{StaticResource WarningBrush}");
            ExpectRejected(() => VerifyMarkerStyles(wrong, theme, border, text, binding, file), file + ": a blue token wired to orange must fail");
        }
        var wrongTheme = new XDocument(theme);
        wrongTheme.Descendants(Wpf + "Color").Single(e => (string?)e.Attribute(X + "Key") == "PossibleColor").Value = "#8A4B00";
        var sample = MarkerStyles[0];
        ExpectRejected(() => VerifyMarkerStyles(pages[sample.File], wrongTheme, sample.Border, sample.Text, sample.Binding, sample.File),
            "blue brush name hiding orange color must fail");
        return Task.CompletedTask;
    }

    private static void VerifyMarkerStyles(XDocument page, XDocument theme, string border, string text, string binding, string label)
    {
        VerifyToneStyle(page, theme, text, binding, "Foreground", "Brush", label);
        VerifyToneStyle(page, theme, border, binding, "Background", "LightBrush", label);
    }

    private static void VerifyToneStyle(XDocument page, XDocument theme, string key, string binding, string property, string suffix, string label)
    {
        var style = Style(page, key);
        foreach (var (tone, color) in new[] { ("ok", "Success"), ("possible", "Possible"), ("bad", "Danger") })
        {
            var matches = style.Descendants(Wpf + "DataTrigger").Where(t => (string?)t.Attribute("Value") == tone).ToArray();
            Check.Equal(1, matches.Length, label + "/" + key + ": exactly one active trigger for " + tone);
            var trigger = matches[0];
            string actualBinding = (string?)trigger.Attribute("Binding") ?? "";
            Check.True(actualBinding.Contains(binding, StringComparison.Ordinal), label + "/" + key + ": trigger uses actual context/result binding");
            if (binding == "ContextTone" || binding == "Tone")
                Check.Equal("{Binding " + binding + "}", actualBinding, label + ": no unrelated data path");
            else if (binding.StartsWith("ConnectionContext", StringComparison.Ordinal))
                Check.Contains(actualBinding, "{Binding ContextCheck,", label + ": converter receives the actual marker state");
            var setters = trigger.Elements(Wpf + "Setter").Where(s => (string?)s.Attribute("Property") == property).ToArray();
            Check.Equal(1, setters.Length, label + "/" + key + ": trigger sets " + property);
            string brush = color + suffix;
            Check.Equal("{StaticResource " + brush + "}", (string?)setters[0].Attribute("Value"), label + ": " + tone + " resolves expected brush");
            var (r, g, b) = ResolveColor(theme, brush);
            bool family = color switch { "Success" => g > r && g > b, "Possible" => b > r && b > g, _ => r > g && r > b };
            Check.True(family, label + ": resource " + brush + " really belongs to " + color + " color family");
        }
        var consumers = page.Descendants().Where(e => (string?)e.Attribute("Style") == "{StaticResource " + key + "}").ToArray();
        Check.True(consumers.Length > 0, label + ": color style must actually be used, not a dead resource");
        foreach (var consumer in consumers)
            Check.Null(consumer.Attribute(property), label + ": local " + property + " must not override color triggers");
    }

    private static XElement Style(XDocument page, string key) => page.Descendants(Wpf + "Style")
        .Single(e => (string?)e.Attribute(X + "Key") == key);

    private static (byte R, byte G, byte B) ResolveColor(XDocument theme, string key)
    {
        string value = "{StaticResource " + key + "}";
        for (int depth = 0; value.StartsWith("{StaticResource ", StringComparison.Ordinal) && depth < 5; depth++)
        {
            string resource = value[16..^1];
            var node = theme.Descendants().Single(e => (string?)e.Attribute(X + "Key") == resource);
            value = (string?)node.Attribute("Color") ?? node.Value.Trim();
        }
        Check.True(value.Length == 7 && value[0] == '#', "brush resolves to opaque RGB hex: " + key);
        return (Convert.ToByte(value.Substring(1, 2), 16), Convert.ToByte(value.Substring(3, 2), 16), Convert.ToByte(value.Substring(5, 2), 16));
    }

    private static void ExpectRejected(Action action, string message)
    {
        try { action(); }
        catch (TestFailure) { return; }
        throw new TestFailure(message + ": mutated wiring incorrectly passed");
    }

    private static (XDocument Theme, Dictionary<string, XDocument> Pages) Load([System.Runtime.CompilerServices.CallerFilePath] string caller = "")
    {
        DirectoryInfo? root = new(Path.GetDirectoryName(caller)!);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ProbeDesktop.slnx"))) root = root.Parent;
        if (root is null)
        {
            root = new DirectoryInfo(Environment.CurrentDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "ProbeDesktop.slnx"))) root = root.Parent;
        }
        if (root is null) throw new TestFailure("repository root not found for XAML structure checks");
        string app = Path.Combine(root.FullName, "src", "ProbeDesktop.App");
        return (XDocument.Load(Path.Combine(app, "Themes", "Theme.xaml")),
            MarkerStyles.Select(x => x.File).Append("Views/ConnectionTestCard.xaml")
                .ToDictionary(file => file, file => XDocument.Load(Path.Combine(app, file))));
    }
}
