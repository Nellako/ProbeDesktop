using System.Xml.Linq;

namespace ProbeDesktop.Tests;

/// <summary>
/// 独立验证用例（code-verifier）：帮助页布局修复回归。
/// 只读取 XAML 结构并断言布局契约，不启动界面、不依赖 WPF、不联网、不读真实配置。
/// 修复前 HelpView 根内容为 <c>MaxWidth="1120" HorizontalAlignment="Left"</c>，
/// 在 ScrollViewer 视口内按内容期望宽度左对齐，宽屏下右侧留白、内容被挤向一侧；
/// 修复后为 <c>Stretch</c>，宽于 1120 时由 WPF 居中、窄于 1120 时铺满视口。
/// 断言集在同一份 XAML 上做变异（Left / 去掉 MaxWidth / 打开横向滚动 / 改列宽），
/// 每个变异都必须被拒绝，避免“只读一遍代码就通过”的空断言。
/// </summary>
public static class HelpLayoutTests
{
    private const string HelpRelativePath = "src/ProbeDesktop.App/Views/HelpView.xaml";

    public static async Task V09_HelpPageFillsWidth()
    {
        await Task.CompletedTask;
        string repo = RepositoryRoot();
        XDocument help = Load(Path.Combine(repo, HelpRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        // 1. 真实文件必须满足完整契约（修复后的状态）。
        ValidateHelpLayout(help, "HelpView.xaml");

        // 2. 变异对照：逐项破坏修复点，每一项都必须被同一套断言拒绝。
        AssertRejected(Mutate(help, "HorizontalAlignment", "Left"), "HorizontalAlignment=Left", "stretch");
        AssertRejected(Mutate(help, "HorizontalAlignment", "Center"), "HorizontalAlignment=Center", "stretch");
        AssertRejected(Remove(help, "MaxWidth"), "MaxWidth removed", "MaxWidth stays 1120");
        AssertRejected(Set(help, "HorizontalScrollBarVisibility", "Auto"), "horizontal scrolling enabled", "horizontal scrollbar");
        AssertRejected(Set(help, "VerticalScrollBarVisibility", "Disabled"), "vertical scrolling disabled", "vertical scrollbar");
        AssertRejected(ReplaceColumnWidths(help, "<ColumnDefinition Width=\"Auto\" />"), "auto columns instead of stars", "three columns");

        // 3. 同类宽版页面一致性：所有内容上限 >= 1000 的根容器都必须是 Stretch。
        foreach (string view in Directory.GetFiles(Path.Combine(repo, "src", "ProbeDesktop.App", "Views"), "*.xaml"))
        {
            XDocument doc = Load(view);
            foreach (XElement element in doc.Descendants())
            {
                if (!double.TryParse(Attr(element, "MaxWidth"), out double max) || max < 1000) continue;
                Check.Equal("Stretch", Attr(element, "HorizontalAlignment") ?? "",
                    Path.GetFileName(view) + " wide content (" + element.Name.LocalName + " MaxWidth=" + max + ") must stretch");
            }
        }
    }

    /// <summary>帮助页布局契约：PageRoot 样式 + 纵向滚动 + 1120 上限内横向铺满 + 两栏星号均分 + 卡片不重叠。</summary>
    private static void ValidateHelpLayout(XDocument help, string source)
    {
        string tag = source + ": ";

        XElement userControl = help.Root ?? throw new TestFailure(tag + "no root element");
        Check.Equal("UserControl", userControl.Name.LocalName, tag + "root element");
        Check.Contains(Attr(userControl, "Style") ?? "", "PageRoot", tag + "keeps PageRoot style");

        List<XElement> pageScrolls = userControl.Elements().Where(e => e.Name.LocalName == "ScrollViewer").ToList();
        Check.Equal(1, pageScrolls.Count, tag + "exactly one page-level ScrollViewer");
        XElement scroll = pageScrolls[0];
        Check.Equal("Auto", Attr(scroll, "VerticalScrollBarVisibility"), tag + "vertical scrollbar is Auto");
        Check.Equal("False", Attr(scroll, "Focusable"), tag + "scroll viewer is not focusable");
        string horizontal = Attr(scroll, "HorizontalScrollBarVisibility") ?? "";
        Check.True(horizontal.Length == 0 || horizontal == "Disabled",
            tag + "horizontal scrollbar must stay disabled, was <" + horizontal + ">");

        List<XElement> content = scroll.Elements().ToList();
        Check.Equal(1, content.Count, tag + "ScrollViewer has exactly one content child");
        XElement grid = content[0];
        Check.Equal("Grid", grid.Name.LocalName, tag + "scroll content is a Grid");
        Check.Equal("1120", Attr(grid, "MaxWidth"), tag + "content MaxWidth stays 1120");
        Check.Equal("Stretch", Attr(grid, "HorizontalAlignment"),
            tag + "content must stretch horizontally (Left was the layout defect)");
        Check.Null(Attr(grid, "Width"), tag + "no fixed Width that would defeat stretching");

        List<string> columnWidths = grid.Elements()
            .Where(e => e.Name.LocalName == "Grid.ColumnDefinitions")
            .SelectMany(d => d.Elements().Where(c => c.Name.LocalName == "ColumnDefinition"))
            .Select(c => Attr(c, "Width") ?? "*")
            .ToList();
        Check.Equal("*|20|*", string.Join("|", columnWidths), tag + "three columns: star, 20px gutter, star");

        List<string> rowHeights = grid.Elements()
            .Where(e => e.Name.LocalName == "Grid.RowDefinitions")
            .SelectMany(d => d.Elements().Where(r => r.Name.LocalName == "RowDefinition"))
            .Select(r => Attr(r, "Height") ?? "*")
            .ToList();
        Check.Equal(8, rowHeights.Count, tag + "eight declared rows");
        Check.Equal(8, rowHeights.Count(h => h == "Auto"), tag + "all rows sized Auto");

        List<XElement> blocks = grid.Elements()
            .Where(e => Attr(e, "Grid.Row") is not null)
            .ToList();
        Check.True(blocks.Count >= 9, tag + "expected at least nine positioned blocks, found " + blocks.Count);

        var occupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement block in blocks)
        {
            int row = int.Parse(Attr(block, "Grid.Row")!);
            int column = int.Parse(Attr(block, "Grid.Column") ?? "0");
            int span = int.Parse(Attr(block, "Grid.ColumnSpan") ?? "1");
            Check.True(row < rowHeights.Count, tag + "row index inside declared rows: " + row);
            Check.True(column + span <= 3, tag + "column span fits three columns at row " + row);
            Check.True(occupied.Add(row + ":" + column),
                tag + "no two blocks overlap at row " + row + " column " + column);
        }

        // 整行卡片占满三列；分栏行必须同时占第 0 与第 2 列，不留半行空档（Stretch 后才会被看到）。
        foreach (IGrouping<int, XElement> row in blocks.GroupBy(b => int.Parse(Attr(b, "Grid.Row")!)))
        {
            if (row.Any(b => (Attr(b, "Grid.ColumnSpan") ?? "1") == "3")) continue;
            Check.Equal("0|2", string.Join("|", row.Select(b => Attr(b, "Grid.Column") ?? "0").OrderBy(x => x)),
                tag + "split row " + row.Key + " must fill both star columns");
        }
    }

    private static void AssertRejected(XDocument mutated, string label, string expectedFragment)
    {
        try
        {
            ValidateHelpLayout(mutated, "mutation");
        }
        catch (TestFailure ex)
        {
            Check.Contains(ex.Message, expectedFragment, "mutation '" + label + "' must fail on the expected assertion");
            return;
        }
        throw new TestFailure("mutation '" + label + "' was NOT rejected: assertions are not load-bearing");
    }

    // ---- 变异工具：只在内存中改 XDocument，不写盘、不碰生产文件 ----

    private static XElement HelpContent(XDocument doc) =>
        doc.Root!.Elements().First(e => e.Name.LocalName == "ScrollViewer").Elements().First();

    private static XDocument Clone(XDocument doc) => XDocument.Parse(doc.ToString(SaveOptions.DisableFormatting));

    private static XDocument Set(XDocument doc, string attribute, string value)
    {
        XDocument copy = Clone(doc);
        XElement scroll = copy.Root!.Elements().First(e => e.Name.LocalName == "ScrollViewer");
        scroll.SetAttributeValue(attribute, value);
        return copy;
    }

    private static XDocument Mutate(XDocument doc, string attribute, string value)
    {
        XDocument copy = Clone(doc);
        HelpContent(copy).SetAttributeValue(attribute, value);
        return copy;
    }

    private static XDocument Remove(XDocument doc, string attribute)
    {
        XDocument copy = Clone(doc);
        HelpContent(copy).SetAttributeValue(attribute, null);
        return copy;
    }

    private static XDocument ReplaceColumnWidths(XDocument doc, string columnDefinition)
    {
        XDocument copy = Clone(doc);
        XElement definitions = HelpContent(copy).Elements().First(e => e.Name.LocalName == "Grid.ColumnDefinitions");
        definitions.ReplaceNodes(XElement.Parse(
            "<Grid.ColumnDefinitions xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">"
            + columnDefinition + columnDefinition + columnDefinition + "</Grid.ColumnDefinitions>").Elements());
        return copy;
    }

    private static XDocument Load(string path) => XDocument.Load(path, LoadOptions.SetLineInfo);

    /// <summary>按本地名取属性，避免依赖 XAML 命名空间前缀。</summary>
    private static string? Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string caller = "")
    {
        DirectoryInfo? dir = new(Path.GetDirectoryName(caller)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProbeDesktop.slnx"))) dir = dir.Parent;
        if (dir is null) throw new TestFailure("repository root not found from " + caller);
        return dir.FullName;
    }
}
