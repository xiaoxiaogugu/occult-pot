namespace OccultPot.Models;

public enum SessionPhase
{
	Idle,
	PrepareEntry,
	PlanRoute,
	EnsureWorld,
	EnterIsland,
	WaitEnter,
	ReadyIsland,
	FindPot,
	WaitFight,
	WaitCampReturn,
	ElixirUse,
	Digging,
	WaitLeave,
	WorldTravel,
	Completed,
	Failed
}
