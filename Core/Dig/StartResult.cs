namespace OccultPot.Core.Dig;

internal readonly struct StartResult
{
	public bool Success { get; init; }

	public string? Error { get; init; }

	public static StartResult Ok()
	{
		return new StartResult
		{
			Success = true
		};
	}

	public static StartResult Failed(string error)
	{
		return new StartResult
		{
			Success = false,
			Error = error
		};
	}
}
