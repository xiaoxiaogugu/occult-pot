using System.Numerics;
using OccultPot.Core;

namespace OccultPot.Core.Dig;

internal readonly struct OccultPotSnapshot
{
	public OccultPotStatus Status { get; init; }

	public Vector3 TargetPosition { get; init; }

	public int RemainingCandidates { get; init; }

	public int HintCount { get; init; }

	public string? LastHint { get; init; }

	public RuntimeStatus? Failure { get; init; }
}
