namespace OccultPot.Core.Dig;

internal readonly struct PotChatEvent
{
	public PotChatEventType Type { get; init; }

	public string RawText { get; init; }

	public CardinalDirection? Direction { get; init; }

	public HintDistance? Distance { get; init; }

	public PotChatEvent(PotChatEventType type, string rawText, CardinalDirection? direction = null, HintDistance? distance = null)
	{
		Type = type;
		RawText = rawText;
		Direction = direction;
		Distance = distance;
	}
}
