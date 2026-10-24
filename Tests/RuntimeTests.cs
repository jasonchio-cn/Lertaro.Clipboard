using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using Lertaro.PluginSdk.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lertaro.Plugins.Clipboard.Tests;

[TestClass]
public class RuntimeTests
{
    [TestMethod]
    public async Task DisabledComponentNeverStartsAndCanEnableAgain()
    {
        var enabled = false; var starts = 0; var stops = 0;
        var runtime = new ComponentRuntime(() => enabled,
            () => { starts++; return Task.CompletedTask; }, () => { stops++; return Task.CompletedTask; });
        await runtime.RefreshAsync();
        Assert.AreEqual(0, starts);
        enabled = true;
        await runtime.RefreshAsync(); await runtime.RefreshAsync();
        Assert.AreEqual(1, starts);
        enabled = false; await runtime.RefreshAsync();
        Assert.AreEqual(1, stops);
        enabled = true; await runtime.RefreshAsync();
        Assert.AreEqual(2, starts);
        await runtime.StopAsync();
        Assert.AreEqual(2, stops);
    }

    [TestMethod]
    public async Task DisableDuringStartStopsTheStartedRuntime()
    {
        var enabled = true; var stops = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ComponentRuntime(() => enabled, async () => { entered.SetResult(); await release.Task; },
            () => { stops++; return Task.CompletedTask; });
        var starting = runtime.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        enabled = false; var disabling = runtime.RefreshAsync(); release.SetResult();
        await Task.WhenAll(starting, disabling).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, stops);
    }

    [TestMethod]
    public async Task ShutdownSerializesWithStartAndCannotRestart()
    {
        var starts = 0; var stops = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ComponentRuntime(() => true, async () => { starts++; entered.SetResult(); await release.Task; },
            () => { stops++; return Task.CompletedTask; });
        var starting = runtime.RefreshAsync(); await entered.Task;
        var closing = runtime.StopAsync(); release.SetResult();
        await Task.WhenAll(starting, closing).WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.RefreshAsync(); await runtime.StopAsync();
        Assert.AreEqual(1, starts); Assert.AreEqual(1, stops);
    }

    [TestMethod]
    public async Task FailedStartCleansUpAndCanRetry()
    {
        var fail = true; var starts = 0; var stops = 0;
        var runtime = new ComponentRuntime(() => true,
            () => { starts++; return fail ? Task.FromException(new IOException("test start failure")) : Task.CompletedTask; },
            () => { stops++; return Task.CompletedTask; });
        await Assert.ThrowsExceptionAsync<IOException>(() => runtime.RefreshAsync());
        Assert.AreEqual(1, stops);
        fail = false; await runtime.RefreshAsync(); await runtime.StopAsync();
        Assert.AreEqual(2, starts); Assert.AreEqual(2, stops);
    }

    [TestMethod]
    public void EnglishResourcesMatchHostCultureAndChineseKeys()
    {
        var provider = new ClipboardTranslations();
        var english = provider.GetTranslations("en-US");
        var chinese = provider.GetTranslations("zh-CN");
        CollectionAssert.AreEquivalent(new[] { "zh-CN", "en-US" }, provider.SupportedCultures.ToArray());
        CollectionAssert.AreEquivalent(chinese.Keys.ToArray(), english.Keys.ToArray());
        Assert.IsTrue(english.Count > 0);
        Assert.AreEqual("Open clipboard history (global hotkey)", english["Clip_OpenHotkey"]);
        CollectionAssert.Contains(TranslationService.GetSupportedCultures(typeof(ClipboardPlugin).Assembly).ToArray(), "en-US");
    }

    [TestMethod]
    public void StaticMetadataMatchesRuntimeWithoutStartingThePlugin()
    {
        var plugin = (ClipboardPlugin)RuntimeHelpers.GetUninitializedObject(typeof(ClipboardPlugin));
        var metadata = typeof(ClipboardPlugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(x => x.Key, x => x.Value);
        Assert.AreEqual(plugin.Name, metadata["Lertaro.Plugin.Name"]);
        Assert.AreEqual(plugin.Description, metadata["Lertaro.Plugin.Description"]);
    }

    [TestMethod]
    public void CompiledPluginReferencesTheDeclaredSdkVersion()
    {
        var reference = typeof(ClipboardPlugin).Assembly.GetReferencedAssemblies().Single(x => x.Name == "Lertaro.PluginSdk");
        Assert.AreEqual(new Version(2, 3, 0, 0), reference.Version);
        Assert.AreEqual(reference.Version, typeof(TranslationService).Assembly.GetName().Version);
    }
}
