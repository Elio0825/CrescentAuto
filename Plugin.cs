using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CrescentAuto;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/crescentauto";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IPlayerState PlayerState { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static ICondition Condition { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IAddonLifecycle AddonLifecycle { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;

    private readonly WindowSystem windowSystem = new("CrescentAuto");
    private readonly ControlWindow controlWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Normalize();

        DailyRoutines = new DailyRoutinesIpcService(PluginInterface, Log);

        GameIntegration = new GameIntegrationService(
            Configuration,
            DailyRoutines,
            ClientState,
            CommandManager,
            AddonLifecycle,
            GameGui,
            Log);

        Controller = new AutomationController(
            Configuration,
            GameIntegration,
            Framework,
            ClientState,
            PlayerState,
            ObjectTable,
            Condition,
            ChatGui,
            Log);

        controlWindow = new ControlWindow(this);
        windowSystem.AddWindow(controlWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开新月岛自动重进控制面板，或使用 start/stop/now/enter/status。",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
    }

    public Configuration Configuration { get; }
    public DailyRoutinesIpcService DailyRoutines { get; }
    public GameIntegrationService GameIntegration { get; }
    public AutomationController Controller { get; }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        controlWindow.Dispose();
        Controller.Dispose();
        GameIntegration.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var action = args.Trim().ToLowerInvariant();
        switch (action)
        {
            case "":
                ToggleWindow();
                break;
            case "start":
                Controller.Start();
                break;
            case "stop":
            case "abort":
                Controller.Stop(action == "abort" ? "用户紧急停止" : "用户停止");
                break;
            case "now":
                Controller.RequestImmediateReentry();
                break;
            case "enter":
                Controller.RequestEnterNow();
                break;
            case "status":
                ChatGui.Print($"[新月岛自动重进] {Controller.GetStatusText()}");
                break;
            default:
                ChatGui.Print("[新月岛自动重进] 用法：/crescentauto [start|stop|abort|now|enter|status]");
                break;
        }
    }

    private void ToggleWindow() => controlWindow.Toggle();
}
