using Dalamud.Configuration;
using OccultPot.Core.Data;
using OccultPot.Models;

namespace OccultPot;


public sealed class PluginConfiguration : IPluginConfiguration
{
	public bool Enabled;

	public bool AutoAcceptPartyAtFate = true;

	public bool PreferTp = true;

	public bool UseDiveTp = true;

	public float TpIntervalSeconds = 5f;

	public uint AutoBaseClassJobID;

	public int AutoPhantomJobID = -1;

	public bool SectionRouteExpanded = true;

	public bool SectionDutyExpanded = true;

	public bool SectionRequirementsExpanded;

	public bool SimplifiedUI;

	public float WindowWidth = 560f;

	public float WindowHeight = 460f;

	public float SimplifiedWindowWidth = 360f;

	public float SimplifiedWindowHeight;

	public DataCenterRouteConfig Chocobo = new DataCenterRouteConfig
	{
		Enabled = true
	};

	public DataCenterRouteConfig Moogle = new DataCenterRouteConfig
	{
		Enabled = true
	};

	public DataCenterRouteConfig Cat = new DataCenterRouteConfig
	{
		Enabled = true
	};

	public DataCenterRouteConfig Atomos = new DataCenterRouteConfig
	{
		Enabled = true
	};

	public int Version { get; set; } = 18;

	public DataCenterRouteConfig GetRoute(CnDataCenterKind kind)
	{
		return kind switch
		{
			CnDataCenterKind.Chocobo => Chocobo, 
			CnDataCenterKind.Moogle => Moogle, 
			CnDataCenterKind.Cat => Cat, 
			CnDataCenterKind.Atomos => Atomos, 
			_ => Chocobo, 
		};
	}

	public void SetRoute(CnDataCenterKind kind, DataCenterRouteConfig value)
	{
		switch (kind)
		{
		case CnDataCenterKind.Chocobo:
			Chocobo = value;
			break;
		case CnDataCenterKind.Moogle:
			Moogle = value;
			break;
		case CnDataCenterKind.Cat:
			Cat = value;
			break;
		case CnDataCenterKind.Atomos:
			Atomos = value;
			break;
		}
	}

	public void SyncHomeWorldLock()
	{
		uint homeWorldID = CnWorldCatalog.HomeWorldID;
		CnDataCenterKind? homeDCKind = CnWorldCatalog.HomeDCKind;
		if (homeDCKind.HasValue && homeWorldID != 0)
		{
			GetRoute(homeDCKind.Value).DestinationWorldID = homeWorldID;
		}
	}
}
