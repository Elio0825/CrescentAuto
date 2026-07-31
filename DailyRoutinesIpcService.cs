using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace CrescentAuto;

public enum DependencyCheckResult
{
    Failed,
    Loading,
    Ready,
}

public sealed class DailyRoutinesIpcService
{
    public const string AutoTalkSkipModule = "AutoTalkSkip";
    public const string FieldEntryCommandModule = "FieldEntryCommand";
    public const string InstantLeaveDutyModule = "InstantLeaveDuty";

    private static readonly TimeSpan LoadRequestThrottle = TimeSpan.FromSeconds(2);
    private static readonly string[] EntryModules = [AutoTalkSkipModule, FieldEntryCommandModule];
    private static readonly string[] RequiredModules =
        [AutoTalkSkipModule, FieldEntryCommandModule, InstantLeaveDutyModule];

    private readonly ICallGateSubscriber<string, bool?> isModuleEnabled;
    private readonly ICallGateSubscriber<string, bool, bool> loadModule;
    private readonly IPluginLog log;
    private readonly Dictionary<string, ModuleStatus> moduleStatuses = new(StringComparer.Ordinal)
    {
        [AutoTalkSkipModule] = ModuleStatus.Unknown,
        [FieldEntryCommandModule] = ModuleStatus.Unknown,
        [InstantLeaveDutyModule] = ModuleStatus.Unknown,
    };
    private readonly Dictionary<string, DateTimeOffset> nextLoadRequestAtUtc = new(StringComparer.Ordinal);

    private bool? dailyRoutinesAvailable;

    public DailyRoutinesIpcService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        isModuleEnabled = pluginInterface.GetIpcSubscriber<string, bool?>("DailyRoutines.IsModuleEnabled");
        loadModule = pluginInterface.GetIpcSubscriber<string, bool, bool>("DailyRoutines.LoadModule");
        this.log = log;
    }

    public string StatusText { get; private set; } = "DailyRoutines 模块尚未检查";

    public DependencyCheckResult EnsureEntryModules(bool autoEnable, out string message)
        => EnsureModules(EntryModules, autoEnable, out message);

    public DependencyCheckResult EnsureLeaveModule(bool autoEnable, out string message)
        => EnsureModules([InstantLeaveDutyModule], autoEnable, out message);

    public DependencyCheckResult EnsureAllRequiredModules(bool autoEnable, out string message)
        => EnsureModules(RequiredModules, autoEnable, out message);

    private DependencyCheckResult EnsureModules(
        IReadOnlyCollection<string> moduleNames,
        bool autoEnable,
        out string message)
    {
        try
        {
            var disabledModules = new List<string>();
            foreach (var moduleName in moduleNames)
            {
                var enabled = isModuleEnabled.InvokeFunc(moduleName);
                moduleStatuses[moduleName] = enabled switch
                {
                    true => ModuleStatus.Enabled,
                    false => ModuleStatus.Disabled,
                    null => ModuleStatus.Missing,
                };

                if (enabled is null)
                {
                    dailyRoutinesAvailable = true;
                    message = $"DailyRoutines 中未找到模块 {moduleName}";
                    UpdateStatusText(message);
                    return DependencyCheckResult.Failed;
                }

                if (!enabled.Value)
                    disabledModules.Add(moduleName);
            }

            dailyRoutinesAvailable = true;
            if (disabledModules.Count == 0)
            {
                message = $"DailyRoutines 所需模块已启用：{string.Join("、", moduleNames)}";
                UpdateStatusText("所需模块已就绪");
                return DependencyCheckResult.Ready;
            }

            if (!autoEnable)
            {
                message = $"DailyRoutines 模块未启用：{string.Join("、", disabledModules)}";
                UpdateStatusText("自动启用已关闭");
                return DependencyCheckResult.Failed;
            }

            var now = DateTimeOffset.UtcNow;
            var requestedModules = new List<string>();
            foreach (var moduleName in disabledModules)
            {
                // FieldEntryCommand 声明依赖 AutoTalkSkip，等前置模块生效后再请求主模块。
                if (moduleName == FieldEntryCommandModule
                    && moduleStatuses[AutoTalkSkipModule] != ModuleStatus.Enabled)
                {
                    continue;
                }

                if (nextLoadRequestAtUtc.TryGetValue(moduleName, out var nextRequest) && now < nextRequest)
                    continue;

                nextLoadRequestAtUtc[moduleName] = now.Add(LoadRequestThrottle);
                if (!loadModule.InvokeFunc(moduleName, true))
                {
                    message = $"DailyRoutines 拒绝加载模块 {moduleName}，模块可能不存在或当前账号无权限";
                    UpdateStatusText(message);
                    return DependencyCheckResult.Failed;
                }

                requestedModules.Add(moduleName);
            }

            message = requestedModules.Count > 0
                ? $"已请求 DailyRoutines 启用：{string.Join("、", requestedModules)}，等待模块注册命令"
                : $"正在等待 DailyRoutines 模块启用：{string.Join("、", disabledModules)}";
            UpdateStatusText("正在加载所需模块");
            return DependencyCheckResult.Loading;
        }
        catch (IpcNotReadyError ex)
        {
            dailyRoutinesAvailable = false;
            message = autoEnable
                ? "DailyRoutines IPC 尚不可用，等待插件本体加载"
                : "DailyRoutines 插件本体未启用，且自动启用模块已关闭";
            StatusText = "DailyRoutines 本体未加载或尚未完成初始化";
            log.Debug(ex, "DailyRoutines IPC 尚未就绪");
            return autoEnable ? DependencyCheckResult.Loading : DependencyCheckResult.Failed;
        }
        catch (Exception ex)
        {
            dailyRoutinesAvailable = null;
            message = $"检查 DailyRoutines 模块失败：{ex.Message}";
            StatusText = message;
            log.Error(ex, "检查或启用 DailyRoutines 模块失败");
            return DependencyCheckResult.Failed;
        }
    }

    private void UpdateStatusText(string note)
    {
        var availability = dailyRoutinesAvailable switch
        {
            true => "本体已连接",
            false => "本体未加载",
            _ => "本体状态未知",
        };
        var modules = string.Join("；", RequiredModules.Select(
            moduleName => $"{moduleName}={StatusLabel(moduleStatuses[moduleName])}"));
        StatusText = $"DailyRoutines：{availability}；{modules}。{note}";
    }

    private static string StatusLabel(ModuleStatus status) => status switch
    {
        ModuleStatus.Enabled => "已启用",
        ModuleStatus.Disabled => "未启用",
        ModuleStatus.Missing => "不存在",
        _ => "未检查",
    };

    private enum ModuleStatus
    {
        Unknown,
        Missing,
        Disabled,
        Enabled,
    }
}
