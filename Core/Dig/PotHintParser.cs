using System;
using System.Collections.Generic;
using System.Numerics;

namespace OccultPot.Core.Dig;

internal static class PotHintParser
{
	private static readonly string[] DigDirections = new string[8] { "西北", "西南", "东北", "东南", "正东", "正西", "正南", "正北" };

	internal static bool TryParseChat(string? text, out PotChatEvent evt)
	{
		evt = default(PotChatEvent);
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string text2 = text.Trim();
		if (text2.Contains("有缘再会", StringComparison.Ordinal))
		{
			evt = new PotChatEvent(PotChatEventType.Farewell, text2);
			return true;
		}
		if (text2.Contains("发现了财宝", StringComparison.Ordinal))
		{
			evt = new PotChatEvent(PotChatEventType.TreasureFound, text2);
			return true;
		}
		if (text2.Contains("撒娇罐", StringComparison.Ordinal) && text2.Contains("耗尽", StringComparison.Ordinal) && text2.Contains("力量", StringComparison.Ordinal))
		{
			evt = new PotChatEvent(PotChatEventType.LureExhausted, text2);
			return true;
		}
		if (text2.Contains("更多的圣灵药", StringComparison.Ordinal) && text2.Contains("再帮你找一次", StringComparison.Ordinal))
		{
			evt = new PotChatEvent(PotChatEventType.MoreMedicine, text2);
			return true;
		}
		if (text2.Contains("圣灵药", StringComparison.Ordinal) && (text2.Contains("需要", StringComparison.Ordinal) || text2.Contains("使用", StringComparison.Ordinal) || text2.Contains("没有", StringComparison.Ordinal)))
		{
			evt = new PotChatEvent(PotChatEventType.NeedsMedicine, text2);
			return true;
		}
		if (!text2.Contains("财宝", StringComparison.Ordinal) || !text2.Contains("方向", StringComparison.Ordinal))
		{
			return false;
		}
		string text3 = null;
		string[] digDirections = DigDirections;
		foreach (string text4 in digDirections)
		{
			if (text2.Contains(text4, StringComparison.Ordinal))
			{
				text3 = text4;
				break;
			}
		}
		if (text3 == null || !TryParseDirection(text3, out var direction))
		{
			return false;
		}
		HintDistance? distance = null;
		if (TryParseDistanceFromLine(text2, out var distance2))
		{
			distance = distance2;
		}
		evt = new PotChatEvent(PotChatEventType.DirectionHint, text2, direction, distance);
		return true;
	}

	internal static IReadOnlyList<Vector3> FilterCandidates(Vector3 origin, IReadOnlyList<Vector3> pool, CardinalDirection? direction, HintDistance? distance = null)
	{
		float min = 0f;
		float max = float.PositiveInfinity;
		if (distance.HasValue)
		{
			HintDistance valueOrDefault = distance.GetValueOrDefault();
			GetDistanceRange(valueOrDefault, out min, out max);
			min = Math.Max(0f, min - 3f);
			if (!float.IsPositiveInfinity(max))
			{
				max += 3f;
			}
		}
		List<Vector3> list = new List<Vector3>();
		int num;
		if (direction.HasValue)
		{
			CardinalDirection valueOrDefault2 = direction.GetValueOrDefault();
			num = DirectionSector(valueOrDefault2);
		}
		else
		{
			num = -1;
		}
		int num2 = num;
		foreach (Vector3 item in pool)
		{
			Vector2 delta = new Vector2(item.X - origin.X, item.Z - origin.Z);
			float num3 = delta.LengthSquared();
			if (!(num3 < 1f))
			{
				float num4 = MathF.Sqrt(num3);
				if (!(num4 < min) && !(num4 > max) && (num2 < 0 || DirectionSector(delta) == num2))
				{
					list.Add(item);
				}
			}
		}
		list.Sort(delegate(Vector3 a, Vector3 b)
		{
			float num5 = new Vector2(a.X - origin.X, a.Z - origin.Z).LengthSquared();
			float value = new Vector2(b.X - origin.X, b.Z - origin.Z).LengthSquared();
			return num5.CompareTo(value);
		});
		return list;
	}

	internal static void GetDistanceRange(HintDistance distance, out float min, out float max)
	{
		(min, max) = distance switch
		{
			HintDistance.VeryNear => (0f, 20f), 
			HintDistance.Near => (20f, 100f), 
			HintDistance.Far => (100f, 200f), 
			HintDistance.VeryFar => (200f, float.PositiveInfinity), 
			_ => (0f, float.PositiveInfinity), 
		};
	}

	internal static bool TryParseDirection(string value, out CardinalDirection direction)
	{
		direction = value switch
		{
			"正北" => CardinalDirection.North, 
			"东北" => CardinalDirection.NorthEast, 
			"正东" => CardinalDirection.East, 
			"东南" => CardinalDirection.SouthEast, 
			"正南" => CardinalDirection.South, 
			"西南" => CardinalDirection.SouthWest, 
			"正西" => CardinalDirection.West, 
			"西北" => CardinalDirection.NorthWest, 
			_ => CardinalDirection.North, 
		};
		switch (value)
		{
		case "东南":
		case "正南":
		case "西南":
		case "正西":
		case "正东":
		case "正北":
		case "东北":
		case "西北":
			return true;
		default:
			return false;
		}
	}

	internal static string DirectionLabel(CardinalDirection direction)
	{
		return direction switch
		{
			CardinalDirection.North => "正北", 
			CardinalDirection.NorthEast => "东北", 
			CardinalDirection.East => "正东", 
			CardinalDirection.SouthEast => "东南", 
			CardinalDirection.South => "正南", 
			CardinalDirection.SouthWest => "西南", 
			CardinalDirection.West => "正西", 
			CardinalDirection.NorthWest => "西北", 
			_ => "?", 
		};
	}

	internal static string DistanceLabel(HintDistance distance)
	{
		return distance switch
		{
			HintDistance.VeryNear => "很近", 
			HintDistance.Near => "不远", 
			HintDistance.Far => "稍远", 
			HintDistance.VeryFar => "很远", 
			_ => "?", 
		};
	}

	private static bool TryParseDistanceFromLine(string line, out HintDistance distance)
	{
		if (line.Contains("很近", StringComparison.Ordinal))
		{
			distance = HintDistance.VeryNear;
			return true;
		}
		if (line.Contains("不远", StringComparison.Ordinal))
		{
			distance = HintDistance.Near;
			return true;
		}
		if (line.Contains("稍远", StringComparison.Ordinal))
		{
			distance = HintDistance.Far;
			return true;
		}
		if (line.Contains("很远", StringComparison.Ordinal))
		{
			distance = HintDistance.VeryFar;
			return true;
		}
		distance = HintDistance.VeryNear;
		return false;
	}

	private static int DirectionSector(CardinalDirection direction)
	{
		return direction switch
		{
			CardinalDirection.North => 0, 
			CardinalDirection.NorthEast => 1, 
			CardinalDirection.East => 2, 
			CardinalDirection.SouthEast => 3, 
			CardinalDirection.South => 4, 
			CardinalDirection.SouthWest => 5, 
			CardinalDirection.West => 6, 
			CardinalDirection.NorthWest => 7, 
			_ => -1, 
		};
	}

	private static int DirectionSector(Vector2 delta)
	{
		return ((int)MathF.Floor((MathF.Atan2(delta.X, 0f - delta.Y) + (float)Math.PI / 8f) / ((float)Math.PI / 4f)) + 8) % 8;
	}
}
