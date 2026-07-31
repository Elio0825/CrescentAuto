using System;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CrescentAuto;

public enum IntegrationActionResult
{
    Failed,
    DependencyLoading,
    Simulated,
    Dispatched,
}

public sealed unsafe class GameIntegrationService : IDisposable
{
    private const string ConfirmAddonName = "ContentsFinderConfirm";
    private static readonly TimeSpan AutoCommenceThrottle = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PopulationRefreshThrottle = TimeSpan.FromSeconds(5);

    private readonly Configuration configuration;
    private readonly DailyRoutinesIpcService dailyRoutines;
    private readonly IClientState clientState;
    private readonly ICommandManager commandManager;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    private DateTimeOffset nextAutoCommenceAtUtc;
    private DateTimeOffset nextPopulationRefreshAtUtc;

    public GameIntegrationService(
        Configuration configuration,
        DailyRoutinesIpcService dailyRoutines,
        IClientState clientState,
        ICommandManager commandManager,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.dailyRoutines = dailyRoutines;
        this.clientState = clientState;
        this.commandManager = commandManager;
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.log = log;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, ConfirmAddonName, OnConfirmAddonEvent);
        addonLifecycle.RegisterListener(AddonEvent.PreDraw, ConfirmAddonName, OnConfirmAddonEvent);
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(OnConfirmAddonEvent);
    }

    public bool TryGetInstancePlayerCount(out uint count, out string message)
    {
        count = 0;
        if (!AutomationController.IsIslandTerritory(clientState.TerritoryType))
        {
            message = "当前不在新月岛区域";
            return false;
        }

        try
        {
            RequestContentMemberRefreshIfDue();

            var proxy = InfoProxyContentMember.Instance();
            if (proxy == null)
            {
                message = "区域成员列表尚未初始化";
                return false;
            }

            count = proxy->EntryCount;
            if (count == 0)
            {
                message = "区域成员列表尚未收到有效数据";
                return false;
            }

            message = $"区域人数：{count}";
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "读取新月岛区域人数失败");
            message = $"读取区域人数失败：{ex.Message}";
            return false;
        }
    }

    public bool TryGetDutyTimeRemaining(out TimeSpan remaining, out string message)
    {
        remaining = TimeSpan.Zero;
        if (!AutomationController.IsIslandTerritory(clientState.TerritoryType))
        {
            message = "当前不在新月岛区域";
            return false;
        }

        try
        {
            var eventFramework = EventFramework.Instance();
            if (eventFramework == null)
            {
                message = "事件框架尚未初始化";
                return false;
            }

            var director = eventFramework->GetContentDirector();
            if (director == null)
            {
                message = "未找到当前副本控制器";
                return false;
            }

            var seconds = director->ContentTimeLeft;
            if (!float.IsFinite(seconds) || seconds <= 0)
            {
                message = "副本计时器尚未提供有效数据";
                return false;
            }

            remaining = TimeSpan.FromSeconds(seconds);
            message = $"副本剩余：{FormatDuration(remaining)}";
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "读取新月岛副本剩余时间失败");
            message = $"读取剩余时间失败：{ex.Message}";
            return false;
        }
    }

    public bool TryCommenceCurrentDuty(out string message, bool bypassThrottle = true)
    {
        try
        {
            var addon = gameGui.GetAddonByName<AddonContentsFinderConfirm>(ConfirmAddonName);
            return TryClickCommence(addon, bypassThrottle, out message);
        }
        catch (Exception ex)
        {
            log.Error(ex, "确认任务出发失败");
            message = $"确认任务出发失败：{ex.Message}";
            return false;
        }
    }

    public IntegrationActionResult DispatchEntry(
        IslandTarget target,
        bool dryRun,
        out string message)
    {
        var command = target == IslandTarget.South
            ? configuration.SouthEntryCommand
            : configuration.NorthEntryCommand;
        return DispatchCommand(command, $"进入{AutomationController.TargetLabel(target)}", dryRun, out message);
    }

    public IntegrationActionResult DispatchLeave(bool dryRun, out string message)
        => DispatchCommand(configuration.ExitCommand, "立即退出副本", dryRun, out message);

    public IntegrationActionResult DispatchPostEntryCommand(string command, bool dryRun, out string message)
        => DispatchCommand(command, "执行进岛后命令", dryRun, out message);

    public IntegrationActionResult EnsureRequiredDailyRoutinesModules(out string message)
        => MapDependencyResult(
            dailyRoutines.EnsureAllRequiredModules(configuration.AutoEnableDailyRoutinesModules, out message));

    private void OnConfirmAddonEvent(AddonEvent _, AddonArgs args)
    {
        if (!configuration.AutoCommenceDuty || args.Addon.IsNull)
            return;

        try
        {
            if (TryClickCommence((AddonContentsFinderConfirm*)args.Addon.Address, false, out var message))
                log.Information("{Message}", message);
        }
        catch (Exception ex)
        {
            log.Error(ex, "自动确认任务出发失败");
        }
    }

    private bool TryClickCommence(
        AddonContentsFinderConfirm* addon,
        bool bypassThrottle,
        out string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (!bypassThrottle && now < nextAutoCommenceAtUtc)
        {
            message = "确认按钮点击节流中";
            return false;
        }

        if (addon == null || !addon->IsVisible)
        {
            message = "当前没有可见的任务出发确认窗口";
            return false;
        }

        if (addon->AtkValues == null || addon->AtkValuesCount <= 7)
        {
            message = "任务出发确认窗口数据尚未就绪";
            return false;
        }

        if (addon->AtkValues[7].UInt != 0)
        {
            message = "当前任务出发状态不允许确认";
            return false;
        }

        var button = addon->CommenceButton;
        if (button == null || button->OwnerNode == null || !button->IsEnabled)
        {
            message = "任务出发按钮尚不可用";
            return false;
        }

        if (!TryClickButton(button))
        {
            message = "任务出发按钮事件尚未就绪";
            return false;
        }

        nextAutoCommenceAtUtc = now.Add(AutoCommenceThrottle);
        message = "已点击任务出发确认";
        return true;
    }

    private IntegrationActionResult DispatchCommand(
        string command,
        string actionName,
        bool dryRun,
        out string message)
    {
        if (!TryValidateCommand(command, out var commandRoot, out var error))
        {
            message = $"{actionName}命令无效：{error}";
            return IntegrationActionResult.Failed;
        }

        var commandAvailable = commandManager.Commands.Keys.Any(
            key => string.Equals(key, commandRoot, StringComparison.OrdinalIgnoreCase));
        if (!commandAvailable)
        {
            var normalizedCommandRoot = commandRoot.ToLowerInvariant();
            DependencyCheckResult dependencyResult;
            switch (normalizedCommandRoot)
            {
                case "/pdrfe":
                    dependencyResult = dailyRoutines.EnsureEntryModules(
                        configuration.AutoEnableDailyRoutinesModules,
                        out message);
                    break;
                case "/pdr":
                    dependencyResult = dailyRoutines.EnsureLeaveModule(
                        configuration.AutoEnableDailyRoutinesModules,
                        out message);
                    break;
                default:
                    message = $"没有找到命令 {commandRoot}";
                    return IntegrationActionResult.Failed;
            }

            if (dependencyResult == DependencyCheckResult.Failed)
                return IntegrationActionResult.Failed;

            if (dependencyResult == DependencyCheckResult.Loading)
                return IntegrationActionResult.DependencyLoading;

            message = $"DailyRoutines 模块已启用，正在等待命令 {commandRoot} 完成注册";
            return IntegrationActionResult.DependencyLoading;
        }

        if (dryRun)
        {
            message = $"Dry Run：将执行 {command}";
            return IntegrationActionResult.Simulated;
        }

        try
        {
            if (!commandManager.ProcessCommand(command))
            {
                message = $"Dalamud 未能派发命令：{command}";
                return IntegrationActionResult.Failed;
            }

            message = $"已派发：{command}";
            return IntegrationActionResult.Dispatched;
        }
        catch (Exception ex)
        {
            log.Error(ex, "执行命令时发生异常：{Command}", command);
            message = $"执行命令时发生异常：{ex.Message}";
            return IntegrationActionResult.Failed;
        }
    }

    private static IntegrationActionResult MapDependencyResult(DependencyCheckResult result) => result switch
    {
        DependencyCheckResult.Ready => IntegrationActionResult.Dispatched,
        DependencyCheckResult.Loading => IntegrationActionResult.DependencyLoading,
        _ => IntegrationActionResult.Failed,
    };

    private static bool TryClickButton(AtkComponentButton* button)
    {
        var ownerNode = button->OwnerNode;
        if (ownerNode == null)
            return false;

        var unitManager = RaptureAtkUnitManager.Instance();
        var ownerAddon = unitManager == null
            ? null
            : unitManager->GetAddonByNode(&ownerNode->AtkResNode);
        var eventData = ownerNode->AtkResNode.AtkEventManager.Event;
        if (ownerAddon == null || eventData == null)
            return false;

        ownerAddon->ReceiveEvent(
            eventData->State.EventType,
            (int)eventData->Param,
            eventData,
            null);
        return true;
    }

    private void RequestContentMemberRefreshIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextPopulationRefreshAtUtc)
            return;

        nextPopulationRefreshAtUtc = now.AddSeconds(1);
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return;

        var agent = agentModule->GetAgentByInternalId(AgentId.ContentMemberList);
        if (agent == null || agent->IsAgentActive())
            return;

        var returnValue = new AtkValue();
        var parameter = new AtkValue();
        parameter.SetInt(1);
        agent->ReceiveEvent(&returnValue, &parameter, 1, 0);
        nextPopulationRefreshAtUtc = now.Add(PopulationRefreshThrottle);
    }

    private static bool TryValidateCommand(string command, out string root, out string error)
    {
        root = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            error = "命令为空";
            return false;
        }

        if (command.IndexOfAny(['\r', '\n']) >= 0)
        {
            error = "命令不能包含换行";
            return false;
        }

        var trimmed = command.Trim();
        if (!trimmed.StartsWith('/'))
        {
            error = "命令必须以 / 开头";
            return false;
        }

        root = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return true;
    }

    public static string FormatDuration(TimeSpan value)
        => value.TotalHours >= 1
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
}
