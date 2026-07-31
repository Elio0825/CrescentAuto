namespace CrescentAuto;

public enum IslandTarget
{
    South,
    North,
}

public enum TriggerMode
{
    RemainingTime,
    LowPopulation,
    Either,
}

public enum AutomationState
{
    Stopped,
    Starting,
    Monitoring,
    Cooldown,
    WaitingForSafeExit,
    ExitDispatched,
    WaitingOutside,
    EntryDispatched,
    DryRunIdle,
    Faulted,
}
