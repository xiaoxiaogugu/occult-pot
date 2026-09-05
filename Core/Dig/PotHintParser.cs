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
		if (text2.Contains("第二处", StringComparison.Ordinal) && text2.Contains("财宝", StringComparison.Ordinal))
		{
			evt = new PotChatEvent(PotChatEventType.ContinuationReady, text2);
			return true;
		}
		if (text2.Contains("打开第一个财宝", StringComparison.Ordinal) && text2.Contains("圣灵药", StringComparison.Ordinal))
		{
			evt = new PotChatEvent(PotChatEventType.ElixirRejected, text2);
			return true;
		}
		if (text2.Contains("圣灵药", StringComparison.Ordinal) && (text2.Contains("给我", StringComparison.Ordinal) || text2.Contains("想要", StringComparison.Ordinal) || text2.Contains("需要", StringComparison.Ordinal) || text2.Contains("没有", StringComparison.Ordinal)))
		{
			evt = new PotChatEvent(PotChatEventType.NeedsMedicine, text2);
			return true;
		}
		if (!text2.Contains("财宝", StringComparison.Ordinal) || !text2.Contains("方向", StringComparison.Ordinal))
			return false;

		string? matched = null;
		foreach (var label in DigDirections)
		{
			if (!text2.Contains(label, StringComparison.Ordinal))
				continue;
			matched = label;
			break;
		}

		if (matched == null || !TryParseDirection(matched, out var direction))
			return false;

		HintDistance? distance = null;
		if (TryParseDistanceFromLine(text2, out var parsedDistance))
			distance = parsedDistance;

		evt = new PotChatEvent(PotChatEventType.DirectionHint, text2, direction, distance);
		return true;
	}

	internal static IReadOnlyList<Vector3> ResolveCandidates(
		Vector3 origin,
		IReadOnlyList<Vector3> primary,
		IReadOnlyList<Vector3> all,
		CardinalDirection? direction,
		HintDistance? distance)
	{
		var filtered = FilterCandidates(origin, primary, direction, distance);
		if (filtered.Count > 0)
			return filtered;

		var pool = all.Count > 0 ? all : primary;
		if (!ReferenceEquals(primary, pool))
		{
			filtered = FilterCandidates(origin, pool, direction, distance);
			if (filtered.Count > 0)
				return filtered;
		}

		if (distance != null)
		{
			filtered = FilterCandidates(origin, pool, direction, null);
			if (filtered.Count > 0)
				return filtered;
		}

		if (direction is { } dir)
		{
			for (var widen = 1; widen <= 2; widen++)
			{
				filtered = FilterByNearbySectors(origin, pool, dir, widen);
				if (filtered.Count > 0)
					return filtered;
			}
		}

		return FilterCandidates(origin, pool, null, null);
	}

	internal static IReadOnlyList<Vector3> FilterCandidates(Vector3 origin, IReadOnlyList<Vector3> pool, CardinalDirection? direction, HintDistance? distance = null)
	{
		float min = 0f;
		float max = float.PositiveInfinity;
		if (distance is { } hintDistance)
		{
			GetDistanceRange(hintDistance, out min, out max);
			min = Math.Max(0f, min - 3f);
			if (!float.IsPositiveInfinity(max))
				max += 3f;
		}

		var sector = direction is { } dir ? DirectionSector(dir) : -1;
		var list = new List<Vector3>();
		foreach (var item in pool)
		{
			var delta = new Vector2(item.X - origin.X, item.Z - origin.Z);
			var distSq = delta.LengthSquared();
			if (distSq < 1f)
				continue;

			var dist = MathF.Sqrt(distSq);
			if (dist < min || dist > max)
				continue;
			if (sector >= 0 && DirectionSector(delta) != sector)
				continue;

			list.Add(item);
		}

		list.Sort((a, b) =>
		{
			var da = new Vector2(a.X - origin.X, a.Z - origin.Z).LengthSquared();
			var db = new Vector2(b.X - origin.X, b.Z - origin.Z).LengthSquared();
			return da.CompareTo(db);
		});
		return list;
	}

	private static IReadOnlyList<Vector3> FilterByNearbySectors(
		Vector3 origin,
		IReadOnlyList<Vector3> pool,
		CardinalDirection direction,
		int widen)
	{
		var center = DirectionSector(direction);
		var list   = new List<Vector3>();
		foreach (var item in pool)
		{
			var delta  = new Vector2(item.X - origin.X, item.Z - origin.Z);
			var distSq = delta.LengthSquared();
			if (distSq < 1f)
				continue;

			var offset = Math.Abs(DirectionSector(delta) - center);
			if (offset > 4)
				offset = 8 - offset;
			if (offset > widen)
				continue;

			list.Add(item);
		}

		list.Sort((a, b) =>
		{
			var da = new Vector2(a.X - origin.X, a.Z - origin.Z).LengthSquared();
			var db = new Vector2(b.X - origin.X, b.Z - origin.Z).LengthSquared();
			return da.CompareTo(db);
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
