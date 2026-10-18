using System.Collections.Concurrent;
using Lertaro.PluginSdk.Abstractions;
using Lertaro.PluginSdk.Abstractions.Plugins;
using Lertaro.PluginSdk.Services;

namespace Lertaro.Plugins.Clipboard;

public sealed class ClipboardProvider : IInstantResultProvider
{
    private static readonly ConcurrentDictionary<string, (ClipRecord Record, PluginPreviewEntry Entry)> Previews = new();
    private static readonly ConcurrentDictionary<string, ClipRecord> Targets = new();
    private readonly ClipboardRuntime _runtime = ClipboardRuntime.Shared.Value;
    public string Name => "剪贴板历史";
    public IReadOnlyList<string> QueryTriggerKeywords => [_runtime.Keyword];
    private const string Icon = "M8,2 L16,2 L16,4 L20,4 L20,23 L4,23 L4,4 L8,4 Z M8,6 L16,6 L16,4 L8,4 Z M7,10 L17,10 M7,14 L17,14 M7,18 L14,18";
    internal static readonly (string Key, string Name)[] Categories =
        [("all", "全部"), ("text", "文本"), ("url", "链接"), ("code", "代码"), ("json", "JSON"), ("image", "图片"), ("files", "文件")];
    internal static string Label(string category) => Categories.FirstOrDefault(x => x.Key == category).Name ?? category;
    internal static bool TryTarget(ISearchResult result, out ClipRecord item) => Targets.TryGetValue(result.InstantActionArgument ?? "", out item!);

    public IEnumerable<InstantResultItem> GetInstantResults(string query)
    {
        if (!TriggerWord.TryMatch(query, _runtime.Keyword, out var rest)) yield break;
        rest = SearchQueryService.StripQueryTokens(rest);
        var (category, search) = ParseFilter(rest);
        var store = _runtime.Store;
        if (store == null)
        { yield return Command(_runtime.Status, "历史尚未就绪", () => { }); yield break; }
        var all = store.Snapshot();
        if (Targets.Count > 300) Targets.Clear();
        var matches = all.Where(x => category == "all" || x.Category == category)
            .Where(x => Matches(x, search)).ToArray();
        var liveIds = all.Select(x => x.Id).ToHashSet();
        foreach (var id in Previews.Keys) if (!liveIds.Contains(id)) Previews.TryRemove(id, out _);
        foreach (var token in Targets.Keys)
            if (Targets.TryGetValue(token, out var target) && !liveIds.Contains(target.Id)) Targets.TryRemove(token, out _);
        foreach (var item in matches.Take(100))
        {
            var cached = Previews.AddOrUpdate(item.Id, _ => MakePreview(item), (_, old) => old.Record == item ? old : MakePreview(item));
            var token = PluginPreviewCache.Register(cached.Entry);
            Targets[token] = item;
            yield return new InstantResultItem
            {
                Title = Short(item.Summary, 160),
                Description = $"{Label(item.Category)} · {item.CopiedAt.ToLocalTime():MM-dd HH:mm} · {item.Source}",
                IconData = item.Kind == ClipKind.Image ? "path:" + store.ImagePath(item) : Icon,
                ActionType = "None", ActionArgument = token,
                OnExecuteFunc = () => { _runtime.Paste(item); return false; }
            };
        }
        if (matches.Length > 100) yield return Command($"共 {matches.Length} 条，显示前 100 条", "请输入更多关键词缩小范围", () => { });
        if (matches.Length == 0) yield return Command("没有匹配的剪贴板历史", "复制内容后自动记录；分类示例：clip /image", () => { });
        yield return Command("打开剪贴板面板", "直接点击类型标签筛选，左右并排查看历史与预览", _runtime.ShowHistory);
        yield return Command("重置顺序粘贴", "清除本轮队列，从现在起收集下一批复制内容", _runtime.ResetSequence);
        yield return Command("清空剪贴板历史…", "删除全部历史和图片缓存", ClipboardMenu.ConfirmClear);
    }
    private (ClipRecord Record, PluginPreviewEntry Entry) MakePreview(ClipRecord item) =>
        (item, new PluginPreviewEntry(Short(item.Summary, 100), Name, new Lazy<System.Windows.Controls.UserControl>(() => new ClipboardPreview(item, _runtime.Store!))));
    internal static bool Matches(ClipRecord item, string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return true;
        var text = item.Kind == ClipKind.Text ? item.Text : string.Join(" ", item.Files) + " " + item.Summary;
        return term.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            text.Contains(word, StringComparison.OrdinalIgnoreCase) || item.Source.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            (word.Length < 100 && text.Length < 32000 && FuzzyMatchService.IsMatch(word, text)));
    }
    internal static (string Category, string Search) ParseFilter(string value)
    {
        var trimmed = value.Trim();
        var space = trimmed.IndexOf(' ');
        var head = space < 0 ? trimmed : trimmed[..space];
        if (head.StartsWith('/') && Categories.Any(x => x.Key == head[1..].ToLowerInvariant()))
            return (head[1..].ToLowerInvariant(), space < 0 ? "" : trimmed[(space + 1)..]);
        return ("all", trimmed);
    }
    internal static string Short(string text, int length) => text.Length <= length ? text : text[..length] + "…";
    private static InstantResultItem Command(string title, string subtitle, Action action) => new()
    { Title = title, Description = subtitle, IconData = Icon, ActionType = "None", OnExecuteFunc = () => { action(); return false; } };
}

public sealed class ClipboardMenu : IDynamicActionProvider
{
    public string Name => "剪贴板操作";
    public string GroupName => "剪贴板";
    public bool CanProvideForInstantResults => true;
    public bool CanProvide(IReadOnlyList<ISearchResult> results) => results.Count == 1 && ClipboardProvider.TryTarget(results[0], out _);
    public IEnumerable<DynamicMenuItem> GetMenuItems(IReadOnlyList<ISearchResult> results, IntPtr hMenu)
    {
        if (!CanProvide(results) || hMenu != IntPtr.Zero || !ClipboardProvider.TryTarget(results[0], out var record)) yield break;
        var runtime = ClipboardRuntime.Shared.Value;
        yield return new() { Text = "粘贴到原窗口", OnExecute = () => runtime.Paste(record), ShortcutHint = "Enter" };
        yield return new() { Text = "仅复制", OnExecute = () => runtime.Copy(record), ShortcutHint = "Ctrl+C" };
        yield return new() { Text = "删除记录", OnExecute = () => runtime.Delete(record.Id), ShortcutHint = "Ctrl+D" };
        yield return new() { Text = "重置顺序粘贴", OnExecute = runtime.ResetSequence };
        foreach (var (key, name) in ClipboardProvider.Categories)
            yield return new() { Text = "筛选 · " + name, OnExecute = () => SearchQueryService.ChangeQuery(runtime.Keyword + " " + (key == "all" ? "" : "/" + key + " "), true) };
        yield return new() { Text = "清空历史…", OnExecute = ConfirmClear };
    }
    public IEnumerable<(string Hotkey, Action Execute)> GetHotkeyActions(IReadOnlyList<ISearchResult> results)
    {
        if (!CanProvide(results) || !ClipboardProvider.TryTarget(results[0], out var record)) yield break;
        yield return ("Ctrl+C", () => ClipboardRuntime.Shared.Value.Copy(record));
        yield return ("Ctrl+D", () => ClipboardRuntime.Shared.Value.Delete(record.Id));
    }
    public void ExecuteCommand(IReadOnlyList<ISearchResult> results, uint commandId, IntPtr ownerHwnd) { }
    public void ClearSession() { }
    internal static void ConfirmClear()
    {
        if (PluginMessageBoxService.Show("删除全部历史和缓存图片？此操作不能撤销。", "清空剪贴板历史",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes)
            ClipboardRuntime.Shared.Value.Clear();
    }
}
