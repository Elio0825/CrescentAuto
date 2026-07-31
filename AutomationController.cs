using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace CrescentAuto;

public sealed class AutomationController : IDisposable
{
    public const uint SouthTerritoryId = 1252;
    public const uint NorthTerritoryId = 1346;

    private static readonly TimeSpan DependencyLoadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DependencyRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PostEntryCommandDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PostEntryCommandRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PostEntryCommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly ConditionFlag[] BlockingConditions =
    [
        ConditionFlag.InCombat,
        ConditionFlag.Casting,
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
        ConditionFlag.Occupied,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78,
        ConditionFlag.LoggingOut,
    ];

    private readonly Configuration configuration;
    private readonly GameIntegrationService integration;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private DateTimeOffset nextEvaluationAtUtc;
    private DateTimeOffset nextPopulationSampleAtUtc;
    private DateTimeOffset nextTimeSampleAtUtc;
    private DateTimeOffset? stateDeadlineUtc;
    private DateTimeOffset? safeSinceUtc;
    private DateTimeOffset? dependencyWaitStartedAtUtc;
    private DateTimeOffset? dependencyPreflightDeadlineUtc;
    private DateTimeOffset nextDependencyPreflightAtUtc;
    private DateTimeOffset? postEntryCommandAtUtc;
    private DateTimeOffset? postEntryCommandDeadlineUtc;
    private readonly List<PendingPostEntryCommand> pendingPostEntryCommands = [];
    private IslandTarget? lastObservedIsland;
    private bool postEntryCommandCompletedForVisit;
    private int exitAttempts;
    private int entryAttempts;

    public AutomationController(
        Configuration configuration,
        GameIntegrationService integration,
        IFramework framework,
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        ICondition condition,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.integration = integration;
        this.framework = framework;
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.chatGui = chatGui;
        this.log = log;
        PostEntryCommandStatus = HasEnabledPostEntryCommands()
            ? "等待进入新月岛"
            : "没有已启用的进岛后命令";

        framework.Update += OnFrameworkUpdate;

        if (configuration.Enabled)
            Start(saveConfiguration: false);
    }

    public AutomationState State { get; private set; } = AutomationState.Stopped;
    public uint? InstancePlayerCount { get; private set; }
    public TimeSpan? DutyTimeRemaining { get; private set; }
    public DateTimeOffset? IslandEnteredAtUtc { get; private set; }
    public DateTimeOffset? LowPopulationSinceUtc { get; private set; }
    public DateTimeOffset? NextActionAtUtc { get; private set; }
    public string MetricsMessage { get; private set; } = "尚未读取岛内数据";
    public string LastTriggerReason { get; private set; } = "尚未触发";
    public string LastMessage { get; private set; } = "未启动";
    public string LastError { get; private set; } = string.Empty;
    public string LastTestResult { get; private set; } = "尚未执行测试";
    public bool? LastTestSucceeded { get; private set; }
    public string PostEntryCommandStatus { get; private set; } = "没有已启用的进岛后命令";

    public bool IsRunning => State is not AutomationState.Stopped
        and not AutomationState.Faulted
        and not AutomationState.DryRunIdle;

    public uint CurrentTerritoryId => clientState.TerritoryType;

    public string CurrentLocationLabel => GetIsland(CurrentTerritoryId) switch
    {
        IslandTarget.South => "南岛",
        IslandTarget.North => "北岛",
        _ => "岛外",
    };

    public string StateLabel => State switch
    {
        AutomationState.Stopped => "已停止",
        AutomationState.Starting => "正在启动",
        AutomationState.Monitoring => "监控中",
        AutomationState.Cooldown => "进入保护期",
        AutomationState.WaitingForSafeExit => "等待安全退出",
        AutomationState.ExitDispatched => "等待退出确认",
        AutomationState.WaitingOutside => "岛外等待",
        AutomationState.EntryDispatched => "等待进入确认",
        AutomationState.DryRunIdle => "Dry Run 已模拟",
        AutomationState.Faulted => "故障停机",
        _ => State.ToString(),
    };

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }

    public void Start(bool saveConfiguration = true)
    {
        configuration.Normalize();
        configuration.Enabled = true;
        if (saveConfiguration)
            configuration.Save();

        State = AutomationState.Starting;
        LastError = string.Empty;
        LastMessage = "等待角色与区域状态就绪";
        LastTriggerReason = "启动";
        IslandEnteredAtUtc = null;
        LowPopulationSinceUtc = null;
        NextActionAtUtc = null;
        stateDeadlineUtc = null;
        safeSinceUtc = null;
        dependencyWaitStartedAtUtc = null;
        exitAttempts = 0;
        entryAttempts = 0;
        nextEvaluationAtUtc = DateTimeOffset.MinValue;
        nextPopulationSampleAtUtc = DateTimeOffset.MinValue;
        nextTimeSampleAtUtc = DateTimeOffset.MinValue;

        log.Information("新月岛自动重进已启动，目标：{Target}", TargetLabel(configuration.Target));
        if (saveConfiguration)
        {
            chatGui.Print($"[新月岛自动重进] 已启动，目标：{TargetLabel(configuration.Target)}。");
            BeginDailyRoutinesPreflight(DateTimeOffset.UtcNow);
        }
    }

    public void Stop(string reason = "用户停止", bool saveConfiguration = true)
    {
        configuration.Enabled = false;
        if (saveConfiguration)
            configuration.Save();

        State = AutomationState.Stopped;
        LastMessage = reason;
        NextActionAtUtc = null;
        stateDeadlineUtc = null;
        safeSinceUtc = null;
        dependencyWaitStartedAtUtc = null;
        dependencyPreflightDeadlineUtc = null;
        LowPopulationSinceUtc = null;
        log.Information("新月岛自动重进已停止：{Reason}", reason);

        if (saveConfiguration)
            chatGui.Print($"[新月岛自动重进] 已停止：{reason}。");
    }

    public void RequestImmediateReentry()
    {
        if (!EnsureReadyForManualAction())
            return;

        if (!IsRunning)
            Start();

        var now = DateTimeOffset.UtcNow;
        if (GetIsland(CurrentTerritoryId) is null)
        {
            BeginWaitingOutside(now, TimeSpan.Zero, "手动立即进入");
            return;
        }

        BeginExit("手动立即重进", now);
    }

    public void RequestEnterNow()
    {
        if (!EnsureReadyForManualAction())
            return;

        if (!IsRunning)
            Start();

        var now = DateTimeOffset.UtcNow;
        var currentIsland = GetIsland(CurrentTerritoryId);
        if (currentIsland == configuration.Target)
        {
            chatGui.Print("[新月岛自动重进] 已经位于目标区域。");
            return;
        }

        if (currentIsland is not null)
        {
            BeginExit("切换目标区域", now);
            return;
        }

        BeginWaitingOutside(now, TimeSpan.Zero, "手动立即进入");
    }

    public void TestReadPopulation()
    {
        var populationSucceeded = integration.TryGetInstancePlayerCount(out var count, out var populationMessage);
        InstancePlayerCount = populationSucceeded ? count : null;
        MetricsMessage = populationMessage;
        ReportTest(populationSucceeded, populationMessage);
    }

    public void TestReadDutyTime()
    {
        var succeeded = integration.TryGetDutyTimeRemaining(out var remaining, out var message);
        DutyTimeRemaining = succeeded ? remaining : null;
        MetricsMessage = message;
        ReportTest(succeeded, message);
    }

    public void TestCommenceDuty()
    {
        var succeeded = integration.TryCommenceCurrentDuty(out var message);
        ReportTest(succeeded, message);
    }

    public void TestEntry(IslandTarget target)
    {
        var result = integration.DispatchEntry(target, configuration.DryRun, out var message);
        ReportIntegrationTest(result, message);
    }

    public void TestImmediateLeave()
    {
        var result = integration.DispatchLeave(configuration.DryRun, out var message);
        ReportIntegrationTest(result, message);
    }

    public void TestDailyRoutinesModules()
    {
        var result = integration.EnsureRequiredDailyRoutinesModules(out var message);
        ReportIntegrationTest(result, message);
    }

    public void TestPostEntryCommands()
    {
        var commands = GetEnabledPostEntryCommands();
        if (commands.Count == 0)
        {
            ReportTest(false, "没有已启用的进岛后命令");
            return;
        }

        var succeeded = 0;
        var loading = false;
        var failures = new List<string>();
        foreach (var command in commands)
        {
            var result = integration.DispatchPostEntryCommand(command, configuration.DryRun, out var message);
            if (result is IntegrationActionResult.Dispatched or IntegrationActionResult.Simulated)
                succeeded++;
            else if (result == IntegrationActionResult.DependencyLoading)
                loading = true;
            else
                failures.Add($"{PostEntryCommandLabel(command)}：{ReplaceBuiltInCommandName(message)}");
        }

        bool? testSucceeded = failures.Count > 0 ? false : loading ? null : true;
        var summary = failures.Count > 0
            ? $"已执行 {succeeded}/{commands.Count}；{string.Join("；", failures)}"
            : loading
                ? $"已执行 {succeeded}/{commands.Count}，其余命令正在等待依赖"
                : $"已执行全部 {commands.Count} 条进岛后命令";
        ReportTest(testSucceeded, summary);
    }

    public void SetCustomPostEntryCommandsEnabled(bool enabled)
    {
        configuration.EnableCustomPostEntryCommands = enabled;
        configuration.Save();
        RefreshPostEntryCommands();
    }

    public void RefreshPostEntryCommands()
    {
        if (!HasEnabledPostEntryCommands())
        {
            CancelPostEntryCommands("没有已启用的进岛后命令");
            return;
        }

        var currentIsland = GetIsland(CurrentTerritoryId);
        if (currentIsland is null)
        {
            PostEntryCommandStatus = "等待进入新月岛";
            return;
        }

        SchedulePostEntryCommands(DateTimeOffset.UtcNow);
    }

    public void TestFullReentry()
    {
        if (!EnsureReadyForManualAction())
        {
            ReportTest(false, "角色未就绪或正在读图");
            return;
        }

        ReportTest(true, "已启动完整退出并重进流程");
        RequestImmediateReentry();
    }

    public string GetStatusText()
    {
        var lowDuration = LowPopulationSinceUtc is null
            ? "未触发"
            : $"{(DateTimeOffset.UtcNow - LowPopulationSinceUtc.Value).TotalSeconds:F0} 秒";
        var nextAction = NextActionAtUtc is null
            ? "无"
            : Math.Max(0, (NextActionAtUtc.Value - DateTimeOffset.UtcNow).TotalSeconds).ToString("F0") + " 秒";
        var population = InstancePlayerCount?.ToString() ?? "未知";
        var time = DutyTimeRemaining is null
            ? "未知"
            : GameIntegrationService.FormatDuration(DutyTimeRemaining.Value);

        return $"状态={StateLabel}，区域={CurrentLocationLabel}({CurrentTerritoryId})，区域人数={population}，副本剩余={time}，低人数持续={lowDuration}，下次动作={nextAction}";
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextEvaluationAtUtc)
            return;

        nextEvaluationAtUtc = now.AddMilliseconds(250);
        Tick(now);
    }

    private void Tick(DateTimeOffset now)
    {
        ProcessDailyRoutinesPreflight(now);

        if (IsTransitioning())
        {
            InstancePlayerCount = null;
            DutyTimeRemaining = null;
            LowPopulationSinceUtc = null;
            MetricsMessage = "区域加载中，读数暂不可用";
            if (State is not AutomationState.Stopped)
                LastMessage = "等待区域加载完成";
            return;
        }

        if (!playerState.IsLoaded || objectTable.LocalPlayer is null)
        {
            ClearIslandMetrics();
            if (State is not AutomationState.Stopped)
                LastMessage = "等待角色登录并加载完成";
            return;
        }

        var currentIsland = GetIsland(CurrentTerritoryId);
        if (currentIsland is not null)
            RefreshIslandMetrics(now);
        else
            ClearIslandMetrics();

        TrackPostEntryCommands(currentIsland, now);

        if (State is AutomationState.Stopped or AutomationState.Faulted or AutomationState.DryRunIdle)
            return;

        if (currentIsland == configuration.Target)
        {
            HandleTargetIsland(now);
            return;
        }

        if (currentIsland is not null)
        {
            HandleWrongIsland(now);
            return;
        }

        HandleOutside(now);
    }

    private void HandleTargetIsland(DateTimeOffset now)
    {
        if (State is AutomationState.Starting
            or AutomationState.EntryDispatched
            or AutomationState.WaitingOutside)
        {
            ConfirmEntered(now);
            return;
        }

        if (State == AutomationState.ExitDispatched)
        {
            if (stateDeadlineUtc <= now)
                RetryExitOrFail(now);
            return;
        }

        if (State == AutomationState.WaitingForSafeExit)
        {
            ProcessSafeExit(now);
            return;
        }

        if (State == AutomationState.Cooldown)
        {
            LowPopulationSinceUtc = null;
            if (NextActionAtUtc <= now)
            {
                State = AutomationState.Monitoring;
                NextActionAtUtc = null;
                LastMessage = "保护期结束，开始监控";
            }
            return;
        }

        if (State != AutomationState.Monitoring)
        {
            ConfirmEntered(now);
            return;
        }

        UpdateLowPopulationTracking(now);
        EvaluateTriggers(now);
    }

    private void HandleWrongIsland(DateTimeOffset now)
    {
        if (State == AutomationState.ExitDispatched)
        {
            if (stateDeadlineUtc <= now)
                RetryExitOrFail(now);
            return;
        }

        if (State == AutomationState.WaitingForSafeExit)
        {
            ProcessSafeExit(now);
            return;
        }

        BeginExit($"当前位于{CurrentLocationLabel}，切换到{TargetLabel(configuration.Target)}", now);
    }

    private void HandleOutside(DateTimeOffset now)
    {
        if (State == AutomationState.EntryDispatched)
        {
            if (stateDeadlineUtc <= now)
                RetryEntryOrFail(now);
            return;
        }

        if (State == AutomationState.WaitingOutside)
        {
            if (NextActionAtUtc <= now)
                DispatchEntry(now);
            return;
        }

        BeginWaitingOutside(
            now,
            State == AutomationState.Starting
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromSeconds(configuration.OutsideDelaySeconds),
            State == AutomationState.Starting ? "启动时位于岛外" : "已确认退出新月岛");
    }

    private void EvaluateTriggers(DateTimeOffset now)
    {
        var timeEnabled = configuration.TriggerMode is TriggerMode.RemainingTime or TriggerMode.Either;
        var populationEnabled = configuration.TriggerMode is TriggerMode.LowPopulation or TriggerMode.Either;

        if (timeEnabled
            && DutyTimeRemaining is not null
            && DutyTimeRemaining.Value <= TimeSpan.FromMinutes(configuration.ReenterWhenRemainingMinutes))
        {
            BeginExit(
                $"副本剩余时间已低于 {configuration.ReenterWhenRemainingMinutes} 分钟",
                now);
            return;
        }

        if (populationEnabled
            && LowPopulationSinceUtc is not null
            && now - LowPopulationSinceUtc.Value >= TimeSpan.FromSeconds(configuration.LowPopulationDurationSeconds))
        {
            BeginExit(
                $"区域人数持续低于 {configuration.PopulationThreshold} 人",
                now);
        }
    }

    private void RefreshIslandMetrics(DateTimeOffset now)
    {
        if (now >= nextTimeSampleAtUtc)
        {
            nextTimeSampleAtUtc = now.AddSeconds(1);
            if (integration.TryGetDutyTimeRemaining(out var remaining, out var message))
            {
                DutyTimeRemaining = remaining;
                MetricsMessage = message;
            }
            else
            {
                DutyTimeRemaining = null;
                MetricsMessage = message;
            }
        }

        if (now < nextPopulationSampleAtUtc)
            return;

        nextPopulationSampleAtUtc = now.AddSeconds(configuration.PopulationSampleSeconds);
        if (integration.TryGetInstancePlayerCount(out var count, out var populationMessage))
        {
            InstancePlayerCount = count;
            MetricsMessage = DutyTimeRemaining is null
                ? populationMessage
                : $"{populationMessage}；副本剩余：{GameIntegrationService.FormatDuration(DutyTimeRemaining.Value)}";
            return;
        }

        InstancePlayerCount = null;
        LowPopulationSinceUtc = null;
        MetricsMessage = populationMessage;
    }

    private void UpdateLowPopulationTracking(DateTimeOffset now)
    {
        var populationEnabled = configuration.TriggerMode is TriggerMode.LowPopulation or TriggerMode.Either;
        if (!populationEnabled
            || InstancePlayerCount is null
            || InstancePlayerCount.Value >= configuration.PopulationThreshold)
        {
            LowPopulationSinceUtc = null;
            return;
        }

        LowPopulationSinceUtc ??= now;
    }

    private void ClearIslandMetrics()
    {
        InstancePlayerCount = null;
        DutyTimeRemaining = null;
        LowPopulationSinceUtc = null;
        MetricsMessage = "当前不在新月岛区域";
    }

    private void BeginExit(string reason, DateTimeOffset now)
    {
        LastTriggerReason = reason;
        LastMessage = "等待角色进入安全状态";
        State = AutomationState.WaitingForSafeExit;
        NextActionAtUtc = null;
        stateDeadlineUtc = null;
        safeSinceUtc = null;
        dependencyWaitStartedAtUtc = null;
        exitAttempts = 0;
        LowPopulationSinceUtc = null;
        log.Information("准备退出新月岛：{Reason}", reason);
    }

    private void ProcessSafeExit(DateTimeOffset now)
    {
        if (NextActionAtUtc is { } nextAction && nextAction > now)
            return;

        if (condition.Any(BlockingConditions))
        {
            safeSinceUtc = null;
            LastMessage = "检测到战斗、占用、过场或读图状态，等待解除";
            return;
        }

        safeSinceUtc ??= now;
        var safeDuration = now - safeSinceUtc.Value;
        if (safeDuration < TimeSpan.FromSeconds(configuration.SafeStateSeconds))
        {
            var remaining = configuration.SafeStateSeconds - safeDuration.TotalSeconds;
            LastMessage = $"安全状态确认中，还需 {Math.Max(0, remaining):F0} 秒";
            return;
        }

        DispatchExit(now);
    }

    private void DispatchExit(DateTimeOffset now)
    {
        var result = integration.DispatchLeave(configuration.DryRun, out var message);
        if (result == IntegrationActionResult.DependencyLoading)
        {
            WaitForDependency(now, message);
            return;
        }

        if (result == IntegrationActionResult.Failed)
        {
            Fail(message);
            return;
        }

        dependencyWaitStartedAtUtc = null;
        exitAttempts++;

        if (result == IntegrationActionResult.Simulated)
        {
            State = AutomationState.Cooldown;
            IslandEnteredAtUtc = now;
            NextActionAtUtc = now.AddMinutes(configuration.EntryGracePeriodMinutes);
            LastMessage = "Dry Run：已模拟退出与重进，本轮不发送命令";
            chatGui.Print($"[新月岛自动重进] Dry Run：{LastTriggerReason}。");
            return;
        }

        State = AutomationState.ExitDispatched;
        stateDeadlineUtc = now.AddSeconds(configuration.ExitTimeoutSeconds);
        NextActionAtUtc = stateDeadlineUtc;
        LastMessage = $"{message}，第 {exitAttempts} 次尝试";
    }

    private void RetryExitOrFail(DateTimeOffset now)
    {
        if (exitAttempts < configuration.MaxRetries + 1)
        {
            log.Warning("退出确认超时，准备重试，第 {Attempt} 次", exitAttempts + 1);
            DispatchExit(now);
            return;
        }

        Fail($"退出命令在 {exitAttempts} 次尝试后仍未离开区域");
    }

    private void BeginWaitingOutside(DateTimeOffset now, TimeSpan delay, string reason)
    {
        State = AutomationState.WaitingOutside;
        NextActionAtUtc = now.Add(delay);
        stateDeadlineUtc = null;
        safeSinceUtc = null;
        dependencyWaitStartedAtUtc = null;
        entryAttempts = 0;
        IslandEnteredAtUtc = null;
        LowPopulationSinceUtc = null;
        LastMessage = delay <= TimeSpan.Zero ? "准备进入目标区域" : $"岛外等待 {delay.TotalSeconds:F0} 秒";
        log.Information("进入岛外等待：{Reason}", reason);
    }

    private void DispatchEntry(DateTimeOffset now)
    {
        var result = integration.DispatchEntry(configuration.Target, configuration.DryRun, out var message);
        if (result == IntegrationActionResult.DependencyLoading)
        {
            WaitForDependency(now, message);
            return;
        }

        if (result == IntegrationActionResult.Failed)
        {
            Fail(message);
            return;
        }

        dependencyWaitStartedAtUtc = null;
        entryAttempts++;

        if (result == IntegrationActionResult.Simulated)
        {
            configuration.Enabled = false;
            configuration.Save();
            State = AutomationState.DryRunIdle;
            NextActionAtUtc = null;
            LastMessage = message;
            chatGui.Print($"[新月岛自动重进] {message}");
            return;
        }

        State = AutomationState.EntryDispatched;
        stateDeadlineUtc = now.AddSeconds(configuration.EntryTimeoutSeconds);
        NextActionAtUtc = stateDeadlineUtc;
        LastMessage = $"{message}，第 {entryAttempts} 次尝试";
    }

    private void RetryEntryOrFail(DateTimeOffset now)
    {
        if (entryAttempts < configuration.MaxRetries + 1)
        {
            log.Warning("进入确认超时，准备重试，第 {Attempt} 次", entryAttempts + 1);
            DispatchEntry(now);
            return;
        }

        Fail($"进入命令在 {entryAttempts} 次尝试后仍未到达{TargetLabel(configuration.Target)}");
    }

    private void ConfirmEntered(DateTimeOffset now)
    {
        State = AutomationState.Cooldown;
        IslandEnteredAtUtc = now;
        LowPopulationSinceUtc = null;
        nextPopulationSampleAtUtc = now;
        nextTimeSampleAtUtc = now;
        NextActionAtUtc = now.AddMinutes(configuration.EntryGracePeriodMinutes);
        stateDeadlineUtc = null;
        safeSinceUtc = null;
        dependencyWaitStartedAtUtc = null;
        exitAttempts = 0;
        entryAttempts = 0;
        LastMessage = $"已确认进入{TargetLabel(configuration.Target)}，保护期 {configuration.EntryGracePeriodMinutes} 分钟";
        log.Information("已确认进入{Target}，区域 ID：{TerritoryId}", TargetLabel(configuration.Target), CurrentTerritoryId);
    }

    private void Fail(string error)
    {
        configuration.Enabled = false;
        configuration.Save();
        State = AutomationState.Faulted;
        LastError = error;
        LastMessage = "已自动停机";
        NextActionAtUtc = null;
        stateDeadlineUtc = null;
        safeSinceUtc = null;
        dependencyWaitStartedAtUtc = null;
        dependencyPreflightDeadlineUtc = null;
        log.Error("新月岛自动重进故障停机：{Error}", error);
        chatGui.Print($"[新月岛自动重进] 故障停机：{error}。");
    }

    private void WaitForDependency(DateTimeOffset now, string message)
    {
        dependencyWaitStartedAtUtc ??= now;
        if (now - dependencyWaitStartedAtUtc.Value >= DependencyLoadTimeout)
        {
            Fail($"{message}；等待 DailyRoutines 模块或命令注册超过 {DependencyLoadTimeout.TotalSeconds:F0} 秒，"
                + "请确认 DailyRoutines 本体已启用，并检查模块权限");
            return;
        }

        NextActionAtUtc = now.Add(DependencyRetryDelay);
        LastMessage = $"{message}，将在 {DependencyRetryDelay.TotalSeconds:F0} 秒后复查";
    }

    private void BeginDailyRoutinesPreflight(DateTimeOffset now)
    {
        dependencyPreflightDeadlineUtc = now.Add(DependencyLoadTimeout);
        nextDependencyPreflightAtUtc = now;
        ProcessDailyRoutinesPreflight(now, announceLoading: true);
    }

    private void ProcessDailyRoutinesPreflight(DateTimeOffset now, bool announceLoading = false)
    {
        if (dependencyPreflightDeadlineUtc is null || now < nextDependencyPreflightAtUtc)
            return;

        var result = integration.EnsureRequiredDailyRoutinesModules(out var message);
        if (result == IntegrationActionResult.Dispatched)
        {
            dependencyPreflightDeadlineUtc = null;
            chatGui.Print($"[新月岛自动重进] DailyRoutines 检查完成：{message}。");
            return;
        }

        if (result == IntegrationActionResult.Failed)
        {
            dependencyPreflightDeadlineUtc = null;
            chatGui.Print($"[新月岛自动重进] DailyRoutines 检查失败：{message}。");
            return;
        }

        if (dependencyPreflightDeadlineUtc <= now)
        {
            dependencyPreflightDeadlineUtc = null;
            chatGui.Print($"[新月岛自动重进] DailyRoutines 检查超时：{message}。");
            return;
        }

        nextDependencyPreflightAtUtc = now.Add(DependencyRetryDelay);
        if (announceLoading)
            chatGui.Print($"[新月岛自动重进] DailyRoutines 检查：{message}。");
    }

    private void TrackPostEntryCommands(IslandTarget? currentIsland, DateTimeOffset now)
    {
        if (currentIsland != lastObservedIsland)
        {
            lastObservedIsland = currentIsland;
            postEntryCommandCompletedForVisit = false;
            if (currentIsland is not null && HasEnabledPostEntryCommands())
                SchedulePostEntryCommands(now);
            else
                CancelPostEntryCommands(HasEnabledPostEntryCommands()
                    ? "等待进入新月岛"
                    : "没有已启用的进岛后命令");
        }

        if (currentIsland is null
            || postEntryCommandCompletedForVisit
            || postEntryCommandAtUtc is null
            || now < postEntryCommandAtUtc.Value)
        {
            return;
        }

        foreach (var pending in pendingPostEntryCommands.Where(command => !command.Completed))
        {
            var result = integration.DispatchPostEntryCommand(
                pending.Command,
                configuration.DryRun,
                out var message);
            if (result is IntegrationActionResult.Dispatched or IntegrationActionResult.Simulated)
            {
                pending.Completed = true;
                pending.LastError = string.Empty;
            }
            else
            {
                pending.LastError = ReplaceBuiltInCommandName(message);
            }
        }

        var completed = pendingPostEntryCommands.Count(command => command.Completed);
        if (completed == pendingPostEntryCommands.Count)
        {
            postEntryCommandCompletedForVisit = true;
            postEntryCommandAtUtc = null;
            postEntryCommandDeadlineUtc = null;
            PostEntryCommandStatus = $"已执行全部 {completed} 条进岛后命令";
            chatGui.Print($"[新月岛自动重进] {PostEntryCommandStatus}。");
            return;
        }

        if (postEntryCommandDeadlineUtc <= now)
        {
            postEntryCommandCompletedForVisit = true;
            postEntryCommandAtUtc = null;
            postEntryCommandDeadlineUtc = null;
            var failed = pendingPostEntryCommands
                .Where(command => !command.Completed)
                .Select(command => $"{PostEntryCommandLabel(command.Command)}（{command.LastError}）");
            PostEntryCommandStatus = $"已执行 {completed}/{pendingPostEntryCommands.Count}；失败：{string.Join("、", failed)}";
            log.Warning("进岛后命令执行未全部成功：{Status}", PostEntryCommandStatus);
            chatGui.Print($"[新月岛自动重进] {PostEntryCommandStatus}。");
            return;
        }

        postEntryCommandAtUtc = now.Add(PostEntryCommandRetryDelay);
        PostEntryCommandStatus = $"已执行 {completed}/{pendingPostEntryCommands.Count}，"
            + $"其余命令将在 {PostEntryCommandRetryDelay.TotalSeconds:F0} 秒后重试";
    }

    private void SchedulePostEntryCommands(DateTimeOffset now)
    {
        pendingPostEntryCommands.Clear();
        pendingPostEntryCommands.AddRange(GetEnabledPostEntryCommands().Select(command => new PendingPostEntryCommand(command)));
        if (pendingPostEntryCommands.Count == 0)
        {
            postEntryCommandCompletedForVisit = true;
            CancelPostEntryCommands("没有已启用的进岛后命令");
            return;
        }

        postEntryCommandCompletedForVisit = false;
        postEntryCommandAtUtc = now.Add(PostEntryCommandDelay);
        postEntryCommandDeadlineUtc = postEntryCommandAtUtc.Value.Add(PostEntryCommandTimeout);
        PostEntryCommandStatus = $"将在 {PostEntryCommandDelay.TotalSeconds:F0} 秒后执行 "
            + $"{pendingPostEntryCommands.Count} 条进岛后命令";
    }

    private void CancelPostEntryCommands(string status)
    {
        postEntryCommandAtUtc = null;
        postEntryCommandDeadlineUtc = null;
        pendingPostEntryCommands.Clear();
        PostEntryCommandStatus = status;
    }

    private IReadOnlyList<string> GetEnabledPostEntryCommands()
        => configuration.PostEntryCommands
            .Where(command => command.Enabled
                && (command.BuiltIn || configuration.EnableCustomPostEntryCommands)
                && !string.IsNullOrWhiteSpace(command.Command))
            .Select(command => command.Command.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Configuration.MaxPostEntryCommands)
            .ToArray();

    private bool HasEnabledPostEntryCommands()
        => GetEnabledPostEntryCommands().Count > 0;

    private static string PostEntryCommandLabel(string command)
        => string.Equals(command, Configuration.DefaultPostEntryCommand, StringComparison.OrdinalIgnoreCase)
            ? "非法模式"
            : command;

    private static string ReplaceBuiltInCommandName(string message)
        => message.Replace("/bocchiillegal", "非法模式", StringComparison.OrdinalIgnoreCase);

    private void ReportIntegrationTest(IntegrationActionResult result, string message)
    {
        bool? succeeded = result switch
        {
            IntegrationActionResult.Failed => false,
            IntegrationActionResult.DependencyLoading => null,
            _ => true,
        };
        ReportTest(succeeded, message);
    }

    private void ReportTest(bool? succeeded, string message)
    {
        LastTestSucceeded = succeeded;
        var label = succeeded switch
        {
            true => "成功",
            false => "失败",
            _ => "处理中",
        };
        LastTestResult = $"{label}：{message}";
        chatGui.Print($"[新月岛自动重进] 测试{LastTestResult}");
    }

    private bool EnsureReadyForManualAction()
    {
        if (!playerState.IsLoaded || objectTable.LocalPlayer is null)
        {
            chatGui.Print("[新月岛自动重进] 角色尚未加载完成。");
            return false;
        }

        if (IsTransitioning())
        {
            chatGui.Print("[新月岛自动重进] 当前正在读图，请稍后再试。");
            return false;
        }

        return true;
    }

    private bool IsTransitioning()
        => condition.Any(ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51);

    private static IslandTarget? GetIsland(uint territoryId) => territoryId switch
    {
        SouthTerritoryId => IslandTarget.South,
        NorthTerritoryId => IslandTarget.North,
        _ => null,
    };

    public static bool IsIslandTerritory(uint territoryId)
        => territoryId is SouthTerritoryId or NorthTerritoryId;

    public static string TargetLabel(IslandTarget target)
        => target == IslandTarget.South ? "南岛" : "北岛";

    private sealed class PendingPostEntryCommand(string command)
    {
        public string Command { get; } = command;
        public bool Completed { get; set; }
        public string LastError { get; set; } = string.Empty;
    }
}
