namespace OccultPot.Core;

internal enum RuntimeStatusCode
{
    None,
    Literal,

    ErrorCnOnly,
    ErrorEmptyRoute,
    ErrorEntryJobLevel,
    ErrorEntryJobSwitchTimeout,

    SessionStopped,
    SessionNotStarted,

    Plan_OutsidePick,
    Plan_FetchCatalog,
    Plan_CatalogDetail,
    Plan_NextIsland,

    Session_GoWorld,
    Session_GoWorldDetail,
    Session_NextWorld,

    WorldTravel_NextReplan,
    WorldTravel_NextLeave,
    SkipIsland_Replan,
    SkipIsland_Leave,
    WorldTravel_Command,
    WorldTravel_WaitArrive,
    WorldTravel_Timeout,
    WorldTravel_AfterLeave,
    WorldTravel_LeaveBeforeTravel,

    Enter_AlreadyOn,
    Enter_LeaveCurrent,
    Enter_Command,
    Enter_WaitPlayer,
    Enter_WaitPosition,
    Enter_WaitBetweenAreas,
    Enter_WaitLandCamp,
    Enter_CampReady,
    Enter_WaitEnter,
    Enter_EnteredCamp,
    Enter_WaitBaseJob,
    Enter_HubJobBlocked,
    Enter_Timeout,
    Enter_TimeoutWrongIsland,

    Job_SwitchBase,
    Job_SwitchPhantom,
    Job_Ready,
    Job_Switching,

    Find_WaitLocal,
    Find_StartOnline,
    Find_StartFailed,
    Find_NoTerritoryConfig,
    Find_WaitPlayer,
    Find_PathFailed,
    Find_AtSouthPeek,
    Find_AtNorthPeek,
    Find_AtPot,
    Find_WaitOnline,
    Find_WaitTracker,
    Find_PeekPlayers,
    Find_WaitRetry,
    Find_NoPlayersBoth,
    Find_RestartPeek,
    Find_JudgeNorth,
    Find_RetryNorthPeek,
    Find_Stopped,

    Travel_NoPosition,
    Travel_AlreadyAt,
    Travel_WaitMount,
    Travel_ToSource,
    Travel_ToDest,
    Travel_WaitVnav,
    Travel_ReturnIdle,
    Travel_ReturnCamp,
    Travel_Returning,
    Travel_PrepareReturn,
    Travel_ToAetheryte,
    Travel_Ptp,
    Travel_PreparePtp,
    Travel_WaitStopPtp,
    Travel_DismountPtp,
    Travel_WaitIdlePtp,
    Travel_WaitPtpArrive,
    Travel_WaitStopPtpNamed,
    Travel_PtpResend,
    Travel_WaitPtp,
    Travel_WaitStop,
    Travel_WaitIdleWalk,
    Travel_Stopped,

    Fight_Positioned,
    Fight_DismountWait,
    Fight_InProgress,
    Fight_WaitGuide,
    Fight_WaitFate,

    Camp_WaitActionable,
    Camp_WaitLand,
    Camp_WaitBocchi,
    Camp_BocchiReturning,
    Camp_WaitBocchiTimer,

    Dig_ReadyAtCamp,
    Dig_SkipFind,
    Dig_CampTimeout,
    Dig_NoReturn,
    Dig_WaitActionable,
    Dig_InProgress,
    Dig_InProgressDetail,
    Dig_InProgressWaitHint,
    Dig_InProgressWithMedicine,
    Dig_Stopped,

    DigStatus_WaitingMedicine,
    DigStatus_WaitingHint,
    DigStatus_WaitingChest,
    DigStatus_OpeningChest,
    DigStatus_Completed,
    DigStatus_Failed,

    Dig_TerritoryChanged,
    Dig_ElixirTimeout,
    Dig_HintTimeout,

    Leave_FindMiss,
    Leave_NoPotConfig,
    Leave_NoElixir,
    Leave_FateTimeout,
    Leave_DigStartFailed,
    Leave_DigEnd,
    Leave_DigDone,
    Leave_WrongIsland,
    Leave_PlayerUnavailable,
    Leave_FindTimeout,
    Leave_DigTimeout,
    Leave_WaitingIsland,
    Leave_Timeout,

    Correct_Ptp,

    Tracker_NotStarted,
    Tracker_Reset,
    Tracker_CatalogWaiting,
    Tracker_OnlineWaitingPots,
    Tracker_OnlineWaitingFingerprint,
    Tracker_OnlineDetail,
    Tracker_LocalDetail,

    DigOnly_NotStarted,
}

internal readonly struct RuntimeStatus
{
    public RuntimeStatusCode Code { get; init; }

    public object[] Args { get; init; }

    public static RuntimeStatus None => default;

    public bool IsNone => Code == RuntimeStatusCode.None;

    public static RuntimeStatus Of(RuntimeStatusCode code, params object[] args) =>
        new()
        {
            Code = code,
            Args = args,
        };

    public static RuntimeStatus Literal(string text) =>
        Of(RuntimeStatusCode.Literal, text);
}
