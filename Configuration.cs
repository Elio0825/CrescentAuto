using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;

namespace CrescentAuto;

[Serializable]
public sealed class PostEntryCommandConfiguration
{
    public bool Enabled { get; set; } = true;
    public string Command { get; set; } = string.Empty;
    public bool BuiltIn { get; set; }
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int MaxPostEntryCommands = 20;
    public const string DefaultSouthEntryCommand = "/pdrfe ocs";
    public const string DefaultNorthEntryCommand = "/pdrfe ocn";
    public const string DefaultExitCommand = "/pdr leaveduty";
    public const string DefaultPostEntryCommand = "/bocchiillegal on";

    public int Version { get; set; } = 7;
    public bool DryRun { get; set; }
    public bool AutoCommenceDuty { get; set; } = true;
    public bool AutoEnableDailyRoutinesModules { get; set; } = true;
    public bool EnableCustomPostEntryCommands { get; set; } = true;
    public IslandTarget Target { get; set; } = IslandTarget.South;
    public TriggerMode TriggerMode { get; set; } = TriggerMode.Either;
    public int ReenterWhenRemainingMinutes { get; set; } = 100;
    public int PopulationThreshold { get; set; } = 20;
    public int PopulationSampleSeconds { get; set; } = 5;
    public int LowPopulationDurationSeconds { get; set; } = 45;
    public int EntryGracePeriodMinutes { get; set; } = 3;
    public int OutsideDelaySeconds { get; set; } = 15;
    public int ExitTimeoutSeconds { get; set; } = 30;
    public int EntryTimeoutSeconds { get; set; } = 90;
    public int SafeStateSeconds { get; set; } = 10;
    public int MaxRetries { get; set; } = 999;
    public string SouthEntryCommand { get; set; } = DefaultSouthEntryCommand;
    public string NorthEntryCommand { get; set; } = DefaultNorthEntryCommand;
    public string ExitCommand { get; set; } = DefaultExitCommand;
    public List<PostEntryCommandConfiguration> PostEntryCommands { get; set; } =
    [
        new()
        {
            Enabled = true,
            Command = DefaultPostEntryCommand,
            BuiltIn = true,
        },
    ];

    public string SelectedEntryCommand => Target == IslandTarget.South
        ? SouthEntryCommand
        : NorthEntryCommand;

    public void Normalize()
    {
        var previousVersion = Version;
        if (previousVersion < 4)
            MigrateVersion4Defaults();
        Version = 7;
        ReenterWhenRemainingMinutes = Math.Clamp(ReenterWhenRemainingMinutes, 1, 480);
        PopulationThreshold = Math.Clamp(PopulationThreshold, 1, 200);
        PopulationSampleSeconds = Math.Clamp(PopulationSampleSeconds, 1, 60);
        LowPopulationDurationSeconds = Math.Clamp(LowPopulationDurationSeconds, 5, 600);
        EntryGracePeriodMinutes = Math.Clamp(EntryGracePeriodMinutes, 1, 120);
        OutsideDelaySeconds = Math.Clamp(OutsideDelaySeconds, 0, 300);
        ExitTimeoutSeconds = Math.Clamp(ExitTimeoutSeconds, 10, 180);
        EntryTimeoutSeconds = Math.Clamp(EntryTimeoutSeconds, 20, 300);
        SafeStateSeconds = Math.Clamp(SafeStateSeconds, 0, 120);
        MaxRetries = Math.Clamp(MaxRetries, 0, 999);
        NormalizePostEntryCommands();
    }

    public void Save()
    {
        Normalize();
        Plugin.PluginInterface.SavePluginConfig(this);
    }

    private void MigrateVersion4Defaults()
    {
        if (ReenterWhenRemainingMinutes == 30)
            ReenterWhenRemainingMinutes = 100;
        if (PopulationThreshold == 8)
            PopulationThreshold = 20;
        if (EntryGracePeriodMinutes == 5)
            EntryGracePeriodMinutes = 3;
        if (SafeStateSeconds == 30)
            SafeStateSeconds = 10;
        if (OutsideDelaySeconds == 20)
            OutsideDelaySeconds = 15;
        if (MaxRetries == 2)
            MaxRetries = 999;
    }

    private void NormalizePostEntryCommands()
    {
        PostEntryCommands ??= [];
        var builtIn = PostEntryCommands.FirstOrDefault(command => command.BuiltIn);
        if (builtIn is null)
        {
            builtIn = new PostEntryCommandConfiguration
            {
                Enabled = true,
                BuiltIn = true,
            };
            PostEntryCommands.Insert(0, builtIn);
        }

        builtIn.Command = DefaultPostEntryCommand;
        if (PostEntryCommands.IndexOf(builtIn) > 0)
        {
            PostEntryCommands.Remove(builtIn);
            PostEntryCommands.Insert(0, builtIn);
        }

        foreach (var duplicate in PostEntryCommands.Where(command => command != builtIn && command.BuiltIn))
            duplicate.BuiltIn = false;
        foreach (var command in PostEntryCommands)
            command.Command ??= string.Empty;

        if (PostEntryCommands.Count > MaxPostEntryCommands)
            PostEntryCommands.RemoveRange(MaxPostEntryCommands, PostEntryCommands.Count - MaxPostEntryCommands);
    }
}
