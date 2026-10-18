using System.Reflection;
using Lertaro.PluginSdk.Abstractions;
using Lertaro.PluginSdk.Abstractions.Plugins;
using Lertaro.PluginSdk.Services;

namespace Lertaro.Plugins.Clipboard;

public sealed class ClipboardPlugin : IPlugin, IConfigurable, IActionProvider
{
    public string Name => "剪贴板历史";
    public string Description => "文本、图片和文件历史；搜索、预览、全局快捷键和按复制顺序粘贴。";
    public ClipboardPlugin() { _ = ClipboardRuntime.Shared.Value; }
    public Task PrepareForSettingsTransferAsync() => ClipboardRuntime.Shared.Value.StopAsync();
    public IEnumerable<ISearchResultAction> GetActions() => [];
    public IEnumerable<IDynamicActionProvider> GetDynamicActionProviders() => [new ClipboardMenu()];
    public PluginConfigSchema GetConfigSchema()
    {
        PluginConfigField Field(string key, ConfigFieldType type, object value) => new()
        { Key = key, LabelKey = "Clip_" + key, FieldType = type, DefaultValue = value };
        var keyword = Field("Keyword", ConfigFieldType.Text, "clip"); keyword.RequireNonEmpty = true; keyword.IsTriggerWord = true;
        var open = Field("OpenHotkey", ConfigFieldType.Hotkey, "Ctrl+Shift+D"); open.RequireModifier = true;
        var next = Field("NextHotkey", ConfigFieldType.Hotkey, "Ctrl+Shift+V"); next.RequireModifier = true;
        return new PluginConfigSchema
        {
            Fields = [keyword, open, next, Field("MaxItems", ConfigFieldType.Integer, 1000),
                Field("Days", ConfigFieldType.Integer, 90), Field("TotalMB", ConfigFieldType.Integer, 256),
                Field("ImageMB", ConfigFieldType.Integer, 128), Field("ItemMB", ConfigFieldType.Integer, 10),
                new() { Key = "Reset", LabelKey = "Clip_Reset", FieldType = ConfigFieldType.Button,
                    OnClick = () => ClipboardRuntime.Shared.Value.ResetSequence() },
                new() { Key = "Clear", LabelKey = "Clip_Clear", FieldType = ConfigFieldType.Button,
                    OnClick = ClipboardMenu.ConfirmClear }],
            OnSave = () => ClipboardRuntime.Shared.Value.ApplySettings()
        };
    }
}

public sealed class ClipboardTranslations : ITranslationProvider
{
    public string Name => "剪贴板翻译";
    public IReadOnlyList<string> SupportedCultures => ["zh-CN", "en"];
    public IReadOnlyDictionary<string, string> GetTranslations(string cultureName) =>
        TranslationService.LoadEmbeddedTranslations(Assembly.GetExecutingAssembly(), cultureName, "Plugin");
}
