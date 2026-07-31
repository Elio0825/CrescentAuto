using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrescentAuto;

public sealed class ControlWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public ControlWindow(Plugin plugin)
        : base("新月岛自动重进###CrescentAutoControl")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 460),
            MaximumSize = new Vector2(760, 820),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawActions();
        ImGui.Separator();

        if (!ImGui.BeginTabBar("CrescentAutoTabs"))
            return;

        if (ImGui.BeginTabItem("运行设置"))
        {
            DrawRunSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("详细设置"))
        {
            DrawDetailedSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("功能测试"))
        {
            DrawTests();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawStatus()
    {
        var controller = plugin.Controller;
        var stateColor = controller.State == AutomationState.Faulted
            ? new Vector4(1f, 0.35f, 0.35f, 1f)
            : controller.IsRunning
                ? new Vector4(0.35f, 0.9f, 0.5f, 1f)
                : new Vector4(0.8f, 0.8f, 0.8f, 1f);
        var population = controller.InstancePlayerCount?.ToString() ?? "未知";
        var remaining = controller.DutyTimeRemaining is null
            ? "未知"
            : GameIntegrationService.FormatDuration(controller.DutyTimeRemaining.Value);

        ImGui.TextColored(stateColor, $"状态：{controller.StateLabel}");
        ImGui.Text($"当前位置：{controller.CurrentLocationLabel}");
        ImGui.SameLine(240);
        ImGui.Text($"目标：{AutomationController.TargetLabel(plugin.Configuration.Target)}");
        ImGui.Text($"区域人数：{population}");
        ImGui.SameLine(240);
        ImGui.Text($"剩余时间：{remaining}");
        ImGui.TextWrapped(controller.LastMessage);

        if (!string.IsNullOrEmpty(controller.LastError))
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"错误：{controller.LastError}");

        if (controller.NextActionAtUtc is not null)
        {
            var next = Math.Max(0, (controller.NextActionAtUtc.Value - DateTimeOffset.UtcNow).TotalSeconds);
            ImGui.Text($"下一动作：约 {next:F0} 秒后");
        }

        if (controller.InstancePlayerCount is null || controller.DutyTimeRemaining is null)
            ImGui.TextDisabled(controller.MetricsMessage);
    }

    private void DrawActions()
    {
        var controller = plugin.Controller;
        if (controller.IsRunning)
        {
            if (ImGui.Button("停止自动运行"))
                controller.Stop();
        }
        else
        {
            if (ImGui.Button("启动自动运行"))
                controller.Start();
        }

        ImGui.SameLine();
        if (ImGui.Button("立即重进"))
            controller.RequestImmediateReentry();

        ImGui.SameLine();
        if (ImGui.Button("立即进入目标区域"))
            controller.RequestEnterNow();
    }

    private void DrawRunSettings()
    {
        var configuration = plugin.Configuration;
        ImGui.Text("目标区域");
        if (ImGui.RadioButton("南岛", configuration.Target == IslandTarget.South))
        {
            configuration.Target = IslandTarget.South;
            configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("北岛", configuration.Target == IslandTarget.North))
        {
            configuration.Target = IslandTarget.North;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Text("触发模式");
        if (ImGui.RadioButton("剩余时间", configuration.TriggerMode == TriggerMode.RemainingTime))
        {
            configuration.TriggerMode = TriggerMode.RemainingTime;
            configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("区域人数", configuration.TriggerMode == TriggerMode.LowPopulation))
        {
            configuration.TriggerMode = TriggerMode.LowPopulation;
            configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("任一满足", configuration.TriggerMode == TriggerMode.Either))
        {
            configuration.TriggerMode = TriggerMode.Either;
            configuration.Save();
        }

        var remainingMinutes = configuration.ReenterWhenRemainingMinutes;
        if (ImGui.InputInt("剩余时间低于（分钟）", ref remainingMinutes))
        {
            configuration.ReenterWhenRemainingMinutes = remainingMinutes;
            configuration.Save();
        }

        var threshold = configuration.PopulationThreshold;
        if (ImGui.InputInt("区域人数低于", ref threshold))
        {
            configuration.PopulationThreshold = threshold;
            configuration.Save();
        }

        var lowDuration = configuration.LowPopulationDurationSeconds;
        if (ImGui.InputInt("低人数持续（秒）", ref lowDuration))
        {
            configuration.LowPopulationDurationSeconds = lowDuration;
            configuration.Save();
        }

        ImGui.Separator();

        var autoCommence = configuration.AutoCommenceDuty;
        if (ImGui.Checkbox("自动确认任务出发", ref autoCommence))
        {
            configuration.AutoCommenceDuty = autoCommence;
            configuration.Save();
        }

        var autoEnableModules = configuration.AutoEnableDailyRoutinesModules;
        if (ImGui.Checkbox("自动启用 DailyRoutines 所需模块", ref autoEnableModules))
        {
            configuration.AutoEnableDailyRoutinesModules = autoEnableModules;
            configuration.Save();
        }

        DrawBuiltInPostEntryCommand();

        var enableCustomCommands = configuration.EnableCustomPostEntryCommands;
        if (ImGui.Checkbox("进岛后执行自定义宏命令", ref enableCustomCommands))
        {
            plugin.Controller.SetCustomPostEntryCommandsEnabled(enableCustomCommands);
        }

        if (enableCustomCommands)
            DrawCustomPostEntryCommands();

        ImGui.TextDisabled(plugin.Controller.PostEntryCommandStatus);
        ImGui.TextWrapped(plugin.DailyRoutines.StatusText);
    }

    private void DrawBuiltInPostEntryCommand()
    {
        var configuration = plugin.Configuration;
        var builtIn = configuration.PostEntryCommands.First(command => command.BuiltIn);
        var enabled = builtIn.Enabled;
        if (ImGui.Checkbox("进岛后自动开启非法模式", ref enabled))
        {
            builtIn.Enabled = enabled;
            SavePostEntryCommands();
        }
    }

    private void DrawCustomPostEntryCommands()
    {
        var configuration = plugin.Configuration;
        var removeIndex = -1;
        for (var index = 0; index < configuration.PostEntryCommands.Count; index++)
        {
            var command = configuration.PostEntryCommands[index];
            if (command.BuiltIn)
                continue;

            ImGui.PushID($"PostEntryCommand{index}");

            var enabled = command.Enabled;
            if (ImGui.Checkbox("##Enabled", ref enabled))
            {
                command.Enabled = enabled;
                SavePostEntryCommands();
            }

            ImGui.SameLine();
            var commandText = command.Command;
            ImGui.SetNextItemWidth(-36);
            if (ImGui.InputText("##Command", ref commandText, 256))
            {
                command.Command = commandText;
                SavePostEntryCommands();
            }

            ImGui.SameLine();
            if (ImGui.Button("X"))
                removeIndex = index;

            ImGui.PopID();
        }

        if (removeIndex >= 0)
        {
            configuration.PostEntryCommands.RemoveAt(removeIndex);
            SavePostEntryCommands();
        }

        if (configuration.PostEntryCommands.Count < Configuration.MaxPostEntryCommands
            && ImGui.Button("+ 添加命令"))
        {
            configuration.PostEntryCommands.Add(new PostEntryCommandConfiguration());
            SavePostEntryCommands();
        }
    }

    private void SavePostEntryCommands()
    {
        plugin.Configuration.Save();
        plugin.Controller.RefreshPostEntryCommands();
    }

    private void DrawDetailedSettings()
    {
        var configuration = plugin.Configuration;
        ImGui.Text("检测参数");

        var sampleSeconds = configuration.PopulationSampleSeconds;
        if (ImGui.InputInt("人数采样间隔（秒）", ref sampleSeconds))
        {
            configuration.PopulationSampleSeconds = sampleSeconds;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.Text("进出流程");

        var graceMinutes = configuration.EntryGracePeriodMinutes;
        if (ImGui.InputInt("进入后保护期（分钟）", ref graceMinutes))
        {
            configuration.EntryGracePeriodMinutes = graceMinutes;
            configuration.Save();
        }

        var safeSeconds = configuration.SafeStateSeconds;
        if (ImGui.InputInt("安全状态持续（秒）", ref safeSeconds))
        {
            configuration.SafeStateSeconds = safeSeconds;
            configuration.Save();
        }

        var outsideDelay = configuration.OutsideDelaySeconds;
        if (ImGui.InputInt("岛外等待（秒）", ref outsideDelay))
        {
            configuration.OutsideDelaySeconds = outsideDelay;
            configuration.Save();
        }

        var exitTimeout = configuration.ExitTimeoutSeconds;
        if (ImGui.InputInt("退出确认超时（秒）", ref exitTimeout))
        {
            configuration.ExitTimeoutSeconds = exitTimeout;
            configuration.Save();
        }

        var entryTimeout = configuration.EntryTimeoutSeconds;
        if (ImGui.InputInt("进入确认超时（秒）", ref entryTimeout))
        {
            configuration.EntryTimeoutSeconds = entryTimeout;
            configuration.Save();
        }

        var maxRetries = configuration.MaxRetries;
        if (ImGui.InputInt("失败重试次数", ref maxRetries))
        {
            configuration.MaxRetries = maxRetries;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.Text("兼容命令");

        var southCommand = configuration.SouthEntryCommand;
        if (ImGui.InputText("南岛进入命令", ref southCommand, 256))
        {
            configuration.SouthEntryCommand = southCommand;
            configuration.Save();
        }

        var northCommand = configuration.NorthEntryCommand;
        if (ImGui.InputText("北岛进入命令", ref northCommand, 256))
        {
            configuration.NorthEntryCommand = northCommand;
            configuration.Save();
        }

        var exitCommand = configuration.ExitCommand;
        if (ImGui.InputText("即刻退本命令", ref exitCommand, 256))
        {
            configuration.ExitCommand = exitCommand;
            configuration.Save();
        }
    }

    private void DrawTests()
    {
        var controller = plugin.Controller;
        var dryRun = plugin.Configuration.DryRun;
        if (ImGui.Checkbox("Dry Run（不发送进出岛与开关命令）", ref dryRun))
        {
            plugin.Configuration.DryRun = dryRun;
            plugin.Configuration.Save();
        }

        ImGui.Separator();

        if (ImGui.Button("读取区域人数"))
            controller.TestReadPopulation();

        ImGui.SameLine();
        if (ImGui.Button("读取剩余时间"))
            controller.TestReadDutyTime();

        ImGui.SameLine();
        if (ImGui.Button("确认当前出发窗口"))
            controller.TestCommenceDuty();

        if (ImGui.Button("进入南岛"))
            controller.TestEntry(IslandTarget.South);

        ImGui.SameLine();
        if (ImGui.Button("进入北岛"))
            controller.TestEntry(IslandTarget.North);

        ImGui.SameLine();
        if (ImGui.Button("即刻退本"))
            controller.TestImmediateLeave();

        ImGui.SameLine();
        if (ImGui.Button("完整重进流程"))
            controller.TestFullReentry();

        if (ImGui.Button("检查/启用 DailyRoutines 模块"))
            controller.TestDailyRoutinesModules();

        ImGui.SameLine();
        if (ImGui.Button("执行全部进岛命令"))
            controller.TestPostEntryCommands();

        var resultColor = plugin.Controller.LastTestSucceeded switch
        {
            true => new Vector4(0.35f, 0.9f, 0.5f, 1f),
            false => new Vector4(1f, 0.35f, 0.35f, 1f),
            _ => new Vector4(0.75f, 0.75f, 0.75f, 1f),
        };
        ImGui.PushStyleColor(ImGuiCol.Text, resultColor);
        ImGui.TextWrapped(controller.LastTestResult);
        ImGui.PopStyleColor();
    }
}
