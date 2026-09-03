using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using OmenTools.OmenService;

namespace OccultPot.Core.Adapters;

internal sealed class TpNavigator
{
	internal const float DefaultIntervalSeconds = 5f;

	internal const string DiveTpCommand = "/xsz-divetp";

	internal const string CoordTpCommand = "/xsz-tp";

	private Vector3? pendingTarget;

	private double lastTpSeconds = double.NegativeInfinity;

	internal string LastDetail { get; private set; } = string.Empty;

	internal bool IsPending
	{
		get
		{
			Vector3? vector = pendingTarget;
			return vector.HasValue;
		}
	}

	internal void Stop()
	{
		pendingTarget = null;
		LastDetail = "已停止 TP";
	}

	internal bool HasArrived(Vector3 target, float radius)
	{
		return LocalPlayerState.DistanceTo3D(target) <= radius;
	}

	internal bool IsTeleporting(Vector3 target, float radius)
	{
		if (pendingTarget is not { } pending)
			return false;
		if (Vector3.Distance(pending, target) > 1.5f)
			return false;
		return !HasArrived(target, radius);
	}

	internal bool MoveTo(Vector3 destination, double nowSeconds, float intervalSeconds = 5f, bool useDiveTp = true)
	{
		if (LocalPlayerState.Object == null)
		{
			LastDetail = "无本地玩家";
			return false;
		}
		if (HasArrived(destination, 3f))
		{
			pendingTarget = null;
			LastDetail = "已在目标点";
			return true;
		}
		var interval = Math.Clamp(intervalSeconds, 0.5f, 60f);
		if (nowSeconds - lastTpSeconds < interval)
		{
			LastDetail = $"TP 冷却中（{interval:0.#}s）";
			pendingTarget = destination;
			return false;
		}
		var command = FormatTpCommand(destination, useDiveTp);
		ExternalCommands.Run(command);
		lastTpSeconds = nowSeconds;
		pendingTarget = destination;
		LastDetail = command;
		return true;
	}

	internal static string FormatTpCommand(Vector3 destination, bool useDiveTp = true)
	{
		string value = (useDiveTp ? "/xsz-divetp" : "/xsz-tp");
		IFormatProvider invariantCulture = CultureInfo.InvariantCulture;
		DefaultInterpolatedStringHandler handler = new DefaultInterpolatedStringHandler(3, 4, invariantCulture);
		handler.AppendFormatted(value);
		handler.AppendLiteral(" ");
		handler.AppendFormatted(destination.X, "0.###");
		handler.AppendLiteral(" ");
		handler.AppendFormatted(destination.Y, "0.###");
		handler.AppendLiteral(" ");
		handler.AppendFormatted(destination.Z, "0.###");
		return string.Create(invariantCulture, ref handler);
	}
}
