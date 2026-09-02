using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Fates;
using OccultPot.Core;
using OccultPot.Core.Data;
using OccultPot.Core.Game;
using OccultPot.Models;
using OmenTools;
using OmenTools.OmenService;

namespace OccultPot.Core.Adapters;

internal sealed class KeitaPotTracker
{
	private struct SyncContext
	{
		public string Fingerprint;

		public ushort Datacenter;

		public uint Territory;

		public uint FateID;

		public int FateTimestamp;

		public uint NorthFateID;

		public uint SouthFateID;

		public PotObs North;

		public PotObs South;

		public readonly bool HasObservation
		{
			get
			{
				if (!North.Observed || North.Spawn <= 0)
				{
					if (South.Observed)
					{
						return South.Spawn > 0;
					}
					return false;
				}
				return true;
			}
		}
	}

	private readonly struct PotObs
	{
		public bool Observed { get; init; }

		public long Spawn { get; init; }

		public long Death { get; init; }

		public long LastSeen { get; init; }

		public static PotObs From(PotState pot)
		{
			return new PotObs
			{
				Observed = pot.LocallyObserved,
				Spawn = pot.SpawnTime,
				Death = pot.DeathTime,
				LastSeen = pot.LastSeenAlive
			};
		}
	}

	private sealed class PotState(ushort territoryID, uint fateID, PotKind kind)
	{
		public bool Alive;

		public long SpawnTime = -1L;

		public long DeathTime;

		public long LastSeenAlive = -1L;

		public bool LocallyObserved;

		public ushort TerritoryID { get; } = territoryID;

		public uint FateID { get; } = fateID;

		public PotKind Kind { get; } = kind;

		public string KindLabel
		{
			get
			{
				if (Kind != PotKind.North)
				{
					return "南罐";
				}
				return "北罐";
			}
		}

		public void Reset()
		{
			Alive = false;
			SpawnTime = -1L;
			DeathTime = 0L;
			LastSeenAlive = -1L;
			LocallyObserved = false;
		}
	}

	private sealed class TrackerRow
	{
		[JsonPropertyName("id")]
		public long RowID { get; set; }

		[JsonPropertyName("territory")]
		public uint Territory { get; set; }

		[JsonPropertyName("datacenter")]
		public uint Datacenter { get; set; }

		[JsonPropertyName("server")]
		public uint Server { get; set; }

		[JsonPropertyName("last_update")]
		public long LastUpdate { get; set; }

		[JsonPropertyName("last_fate")]
		public string LastFateHash { get; set; } = string.Empty;

		[JsonPropertyName("fate")]
		public uint Fate { get; set; }

		[JsonPropertyName("fate_timestamp")]
		public int FateTimestamp { get; set; }

		[JsonPropertyName("pot_history")]
		public string PotHistory { get; set; } = string.Empty;
	}

	private sealed class SharedPot
	{
		[JsonPropertyName("fate_id")]
		public uint FateID { get; set; }

		[JsonPropertyName("spawn_time")]
		public long SpawnTime { get; set; }

		[JsonPropertyName("death_time")]
		public long DeathTime { get; set; }

		[JsonPropertyName("last_seen")]
		public long LastSeen { get; set; }
	}

	private sealed class UploadPot
	{
		[JsonPropertyName("fate_id")]
		public uint FateID { get; set; }

		[JsonPropertyName("spawn_time")]
		public long SpawnTime { get; set; }

		[JsonPropertyName("death_time")]
		public long DeathTime { get; set; }

		[JsonPropertyName("last_seen")]
		public long LastSeen { get; set; }

		public static UploadPot From(uint fateID, PotObs obs)
		{
			return new UploadPot
			{
				FateID = fateID,
				SpawnTime = (obs.Observed ? obs.Spawn : (-1)),
				DeathTime = (obs.Observed ? obs.Death : 0),
				LastSeen = (obs.Observed ? obs.LastSeen : (-1))
			};
		}
	}

	private const string TrackerBaseURL = "https://infi.ovh/api/";

	private const string TrackerTable = "OccultTrackerV3";

	private const string TrackerAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJyb2xlIjoiYW5vbiJ9.Ur6wgi_rD4dr3uLLvbLoaEvfLCu4QFWdrF-uHRtbl_s";

	private const string TrackerVersion = "OccultPot-MagicPot";

	private const long RespawnSeconds = 1800L;

	private const int SyncRefreshSeconds = 60;

	private const int FastRetrySeconds = 5;

	private const int CatalogRefreshSeconds = 60;

	private const int CatalogFastRetrySeconds = 5;

	private const int MissingTrackerChecksBeforeCreate = 2;

	private static readonly HashSet<uint> SouthHornFateIDs = new HashSet<uint>
	{
		1962u, 1963u, 1964u, 1965u, 1966u, 1967u, 1968u, 1969u, 1970u, 1971u,
		1972u
	};

	private static readonly HashSet<uint> NorthHornFateIDs = new HashSet<uint>
	{
		2074u, 2075u, 2076u, 2077u, 2078u, 2079u, 2080u, 2081u, 2082u, 2083u,
		2084u
	};

	private static readonly HttpClient Client = CreateClient();

	private static readonly JsonSerializerOptions JSONOptions = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	};

	private readonly PotState[] pots = new PotState[4]
	{
		new PotState(1252, 1976u, PotKind.North),
		new PotState(1252, 1977u, PotKind.South),
		new PotState(1346, 2072u, PotKind.North),
		new PotState(1346, 2073u, PotKind.South)
	};

	private readonly object syncLock = new object();

	private string lastFingerprint = string.Empty;

	private string missingFingerprint = string.Empty;

	private TrackerRow? currentTracker;

	private int missingTrackerChecks;

	private long lastSyncAt;

	private volatile bool syncInFlight;

	private volatile bool syncRequested;

	private volatile bool hasOnlineData;

	private (uint Territory, long NorthSpawn, long NorthSeen, long SouthSpawn, long SouthSeen)? pendingSync;

	private RuntimeStatus statusLine = RuntimeStatus.Of(RuntimeStatusCode.Tracker_NotStarted);

	private readonly Dictionary<(CnDataCenterKind Dc, ushort Territory), RemoteIslandSnapshot> remote = new Dictionary<(CnDataCenterKind, ushort), RemoteIslandSnapshot>();

	private List<RemoteIslandSnapshot>? pendingCatalog;

	private long lastCatalogAt;

	private volatile bool catalogInFlight;

	private volatile bool hasCatalog;

	private volatile bool catalogFetchFailed;

	internal RuntimeStatus StatusLine => statusLine;

	internal bool HasOnlineData => hasOnlineData;

	internal bool HasCatalog => hasCatalog;

	internal RuntimeStatus CatalogStatus { get; private set; } = RuntimeStatus.None;

	internal void Reset()
	{
		ResetIsland();
		lock (syncLock)
		{
			remote.Clear();
			pendingCatalog = null;
		}
		lastCatalogAt = 0L;
		hasCatalog = false;
		catalogFetchFailed = false;
		CatalogStatus = RuntimeStatus.None;
	}

	internal void ForceCatalogRefresh()
	{
		lastCatalogAt = 0L;
		catalogFetchFailed = true;
	}

	internal void ResetIsland()
	{
		PotState[] array = pots;
		for (int i = 0; i < array.Length; i++)
		{
			array[i].Reset();
		}
		lastFingerprint = string.Empty;
		missingFingerprint = string.Empty;
		currentTracker = null;
		missingTrackerChecks = 0;
		lastSyncAt = 0L;
		syncRequested = false;
		hasOnlineData = false;
		lock (syncLock)
		{
			pendingSync = null;
		}
		statusLine = RuntimeStatus.Of(RuntimeStatusCode.Tracker_Reset);
	}

	internal void Tick()
	{
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		ObserveLocalPots(now);
		if (ZoneIds.IsSupportedIsland((ushort)GameState.TerritoryType))
			TrySyncOnline(now);
		TrySyncCatalog(now);
		ApplyPendingSync();
		ApplyPendingCatalog();
		MergeLocalIntoCatalog(now);
		UpdateStatus(now);
		UpdateCatalogStatus(now);
	}

	internal bool TryGetCatalogTarget(ushort territory, out PotKind kind, out string reason)
	{
		kind = PotKind.North;
		reason = string.Empty;
		CnDataCenterKind? cnDataCenterKind = CnWorldCatalog.KindForWorldID(CnWorldCatalog.CurrentWorldID) ?? CnWorldCatalog.KindForDataCenterID(GameState.CurrentDataCenter);
		if (!cnDataCenterKind.HasValue)
		{
			return false;
		}
		RemoteIslandSnapshot value;
		lock (syncLock)
		{
			if (!remote.TryGetValue((cnDataCenterKind.Value, territory), out value))
			{
				return false;
			}
		}
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		if (!OccultTrackerPlanner.TryComputeNext(value, now, out kind, out var waitSeconds, out var untilGoneSeconds, out var alive))
		{
			return false;
		}
		reason = OccultTrackerPlanner.FormatTarget(kind, waitSeconds, untilGoneSeconds, alive);
		return true;
	}

	internal bool TryGetSoonestTarget(ushort territory, out PotKind kind, out string reason)
	{
		kind = PotKind.North;
		reason = string.Empty;
		long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		bool flag = TryCatalogCompute(territory, now, out var kind2, out var wait, out var untilGone, out var alive);
		bool flag2 = TryLocalCompute(territory, now, out var kind3, out var wait2, out var untilGone2, out var alive2);
		if (flag2 & alive2)
		{
			kind = kind3;
			reason = "本地 " + OccultTrackerPlanner.FormatTarget(kind3, wait2, untilGone2, alive2);
			return true;
		}
		if (flag2 && LocalPredictionTrusted(territory, now))
		{
			kind = kind3;
			reason = "本地 " + OccultTrackerPlanner.FormatTarget(kind3, wait2, untilGone2, alive2);
			return true;
		}
		if (flag)
		{
			kind = kind2;
			reason = "在线表 " + OccultTrackerPlanner.FormatTarget(kind2, wait, untilGone, alive);
			return true;
		}
		if (flag2)
		{
			kind = kind3;
			reason = "本地 " + OccultTrackerPlanner.FormatTarget(kind3, wait2, untilGone2, alive2);
			return true;
		}
		return false;
	}

	internal bool TryPickVisit(IReadOnlyList<(CnDataCenterKind Kind, uint WorldID)> worlds, uint currentWorldID, ushort currentTerritory, out PlannedPotVisit visit, ushort excludeTerritory = 0, CnDataCenterKind? excludeDC = null)
	{
		List<RemoteIslandSnapshot> islands;
		lock (syncLock)
		{
			islands = remote.Values.ToList();
		}
		return OccultTrackerPlanner.TryPickVisit(
			islands,
			worlds,
			currentWorldID,
			currentTerritory,
			DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
			CnWorldCatalog.KindForWorldID(currentWorldID) ?? CnWorldCatalog.KindForDataCenterID(GameState.CurrentDataCenter),
			out visit,
			excludeTerritory,
			excludeDC);
	}

	internal bool TryPickNextVisit(
		IReadOnlyList<(CnDataCenterKind Kind, uint WorldID)> worlds,
		uint currentWorldID,
		ushort currentTerritory,
		out PlannedPotVisit visit,
		ushort excludeTerritory,
		CnDataCenterKind? excludeDC,
		ushort excludePotTerritory,
		PotKind? excludePotKind,
		CnDataCenterKind? excludePotDC)
	{
		List<RemoteIslandSnapshot> islands;
		lock (syncLock)
		{
			islands = remote.Values.ToList();
		}
		return OccultTrackerPlanner.TryPickVisit(
			islands,
			worlds,
			currentWorldID,
			currentTerritory,
			DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
			CnWorldCatalog.KindForWorldID(currentWorldID) ?? CnWorldCatalog.KindForDataCenterID(GameState.CurrentDataCenter),
			out visit,
			excludeTerritory,
			excludeDC,
			excludePotTerritory,
			excludePotKind,
			excludePotDC);
	}

	internal bool TryGetTarget(ushort territory, out PotKind kind, out string reason)
	{
		kind = PotKind.North;
		reason = string.Empty;
		if (!TryGetCurrentPots(territory, out PotState north, out PotState south))
		{
			return false;
		}
		if (north.Alive)
		{
			kind = PotKind.North;
			reason = "北罐进行中";
			return true;
		}
		if (south.Alive)
		{
			kind = PotKind.South;
			reason = "南罐进行中";
			return true;
		}
		PotState potState = null;
		if (north.SpawnTime > 0)
		{
			potState = north;
		}
		if (south.SpawnTime > 0 && (potState == null || south.SpawnTime > potState.SpawnTime))
		{
			potState = south;
		}
		if (potState == null)
		{
			return false;
		}
		PotState potState2 = ((potState == north) ? south : north);
		long num = potState.SpawnTime + 1800;
		long num2 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		kind = potState2.Kind;
		reason = ((num <= num2) ? (potState2.KindLabel + "即将刷新") : $"下个{potState2.KindLabel} {TimeSpan.FromSeconds(num - num2):mm\\:ss}");
		return true;
	}

	private void ObserveLocalPots(long now)
	{
		foreach (IFate item in (IEnumerable<IFate>)DService.Instance().Fate)
		{
			PotState pot = GetPot(item.FateId);
			if (pot != null)
			{
				pot.LastSeenAlive = now;
				pot.SpawnTime = item.StartTimeEpoch;
				pot.LocallyObserved = true;
				if (!pot.Alive)
				{
					pot.Alive = true;
					syncRequested = true;
				}
			}
		}
		PotState[] array = pots;
		foreach (PotState potState in array)
		{
			if (potState.Alive && potState.LastSeenAlive != now)
			{
				potState.Alive = false;
				potState.DeathTime = potState.LastSeenAlive;
				syncRequested = true;
			}
		}
	}

	private void TrySyncOnline(long now)
	{
		if (!syncInFlight && TryBuildContext(out var context))
		{
			int num = (hasOnlineData ? 60 : 5);
			if (syncRequested || context.Fingerprint != lastFingerprint || now - lastSyncAt >= num)
			{
				lastFingerprint = context.Fingerprint;
				lastSyncAt = now;
				syncRequested = false;
				syncInFlight = true;
				SyncAsync(context, now);
			}
		}
	}

	private bool TryBuildContext(out SyncContext context)
	{
		context = default(SyncContext);
		if (!PlayerReader.IsAvailable())
		{
			return false;
		}
		uint territoryType = GameState.TerritoryType;
		if (!TryGetCurrentPots((ushort)territoryType, out PotState north, out PotState south))
		{
			return false;
		}
		uint currentDataCenter = GameState.CurrentDataCenter;
		if (currentDataCenter == 0)
		{
			return false;
		}
		HashSet<uint> hashSet = ((territoryType == 1252) ? SouthHornFateIDs : NorthHornFateIDs);
		uint num = 0u;
		long num2 = 0L;
		foreach (IFate item in (IEnumerable<IFate>)DService.Instance().Fate)
		{
			if (hashSet.Contains(item.FateId) && item.StartTimeEpoch > 0 && item.StartTimeEpoch > num2)
			{
				num2 = item.StartTimeEpoch;
				num = item.FateId;
			}
		}
		if (num == 0)
		{
			return false;
		}
		context = new SyncContext
		{
			Fingerprint = ComputeHash(currentDataCenter, num, (int)num2),
			Datacenter = (ushort)currentDataCenter,
			Territory = territoryType,
			FateID = num,
			FateTimestamp = (int)num2,
			NorthFateID = north.FateID,
			SouthFateID = south.FateID,
			North = PotObs.From(north),
			South = PotObs.From(south)
		};
		return true;
	}

	private async Task SyncAsync(SyncContext context, long now)
	{
		_ = 4;
		try
		{
			TrackerRow[] array = JsonSerializer.Deserialize<TrackerRow[]>(await Client.GetStringAsync($"{"https://infi.ovh/api/"}{"OccultTrackerV3"}?last_fate=eq.{context.Fingerprint}&territory=eq.{context.Territory}"), JSONOptions);
			if ((array == null || array.Length <= 0) && context.Territory == 1252)
			{
				array = JsonSerializer.Deserialize<TrackerRow[]>(await Client.GetStringAsync($"{"https://infi.ovh/api/"}{"OccultTrackerV3"}?last_fate=eq.{context.Fingerprint}&territory=eq.0"), JSONOptions);
			}
			if (array != null && array.Length > 0)
			{
				TrackerRow row = SelectTracker(array);
				SharedPot[] shared = ParseSharedPotHistory(row);
				BindTracker(row);
				QueueSharedPotHistory(shared, context);
				await PatchPotHistoryAsync(row, context, now, shared);
				return;
			}
			TrackerRow trackerRow = currentTracker;
			if (trackerRow != null && trackerRow.RowID > 0 && trackerRow.Territory == context.Territory)
			{
				SharedPot[] shared2 = ParseSharedPotHistory(trackerRow);
				QueueSharedPotHistory(shared2, context);
				await PatchPotHistoryAsync(trackerRow, context, now, shared2);
			}
			else if (context.HasObservation)
			{
				if (missingFingerprint == context.Fingerprint)
				{
					missingTrackerChecks++;
				}
				else
				{
					missingFingerprint = context.Fingerprint;
					missingTrackerChecks = 1;
				}
				if (missingTrackerChecks >= 2)
				{
					TrackerRow trackerRow2 = await CreateRowAsync(context, now);
					if (trackerRow2 != null)
					{
						BindTracker(trackerRow2);
					}
				}
			}
			else
			{
				missingFingerprint = string.Empty;
				missingTrackerChecks = 0;
			}
		}
		catch
		{
		}
		finally
		{
			syncInFlight = false;
		}
	}

	private TrackerRow SelectTracker(TrackerRow[] rows)
	{
		TrackerRow trackerRow = currentTracker;
		TrackerRow[] array;
		if (trackerRow != null && trackerRow.RowID > 0)
		{
			array = rows;
			foreach (TrackerRow trackerRow2 in array)
			{
				if (trackerRow2.RowID == currentTracker.RowID)
				{
					return trackerRow2;
				}
			}
		}
		TrackerRow trackerRow3 = rows[0];
		array = rows;
		foreach (TrackerRow trackerRow4 in array)
		{
			if (trackerRow4.LastUpdate > trackerRow3.LastUpdate || (trackerRow4.LastUpdate == trackerRow3.LastUpdate && trackerRow4.RowID > trackerRow3.RowID))
			{
				trackerRow3 = trackerRow4;
			}
		}
		return trackerRow3;
	}

	private void BindTracker(TrackerRow row)
	{
		currentTracker = row;
		hasOnlineData = true;
		missingFingerprint = string.Empty;
		missingTrackerChecks = 0;
	}

	private static SharedPot[]? ParseSharedPotHistory(TrackerRow row)
	{
		if (string.IsNullOrEmpty(row.PotHistory))
		{
			return null;
		}
		try
		{
			return JsonSerializer.Deserialize<SharedPot[]>(row.PotHistory, JSONOptions);
		}
		catch
		{
			return null;
		}
	}

	private void QueueSharedPotHistory(SharedPot[]? shared, SyncContext context)
	{
		if (shared == null)
		{
			return;
		}
		long item = -1L;
		long item2 = -1L;
		long item3 = -1L;
		long item4 = -1L;
		foreach (SharedPot sharedPot in shared)
		{
			if (sharedPot.FateID == context.NorthFateID)
			{
				item = sharedPot.SpawnTime;
				item2 = sharedPot.LastSeen;
			}
			else if (sharedPot.FateID == context.SouthFateID)
			{
				item3 = sharedPot.SpawnTime;
				item4 = sharedPot.LastSeen;
			}
		}
		lock (syncLock)
		{
			pendingSync = (context.Territory, item, item2, item3, item4);
		}
	}

	private async Task PatchPotHistoryAsync(TrackerRow row, SyncContext context, long now, SharedPot[]? shared)
	{
		if (row.RowID <= 0)
		{
			return;
		}
		bool changed = false;
		bool flag = !string.Equals(row.LastFateHash, context.Fingerprint, StringComparison.Ordinal);
		bool flag2 = row.Fate != context.FateID || row.FateTimestamp != context.FateTimestamp;
		UploadPot uploadPot = MergePot(context.NorthFateID, context.North, shared, ref changed);
		UploadPot uploadPot2 = MergePot(context.SouthFateID, context.South, shared, ref changed);
		if (!changed && !flag && !flag2)
		{
			return;
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>
		{
			["last_fate"] = context.Fingerprint,
			["territory"] = context.Territory,
			["fate"] = context.FateID,
			["fate_timestamp"] = context.FateTimestamp,
			["last_update"] = now
		};
		string potHistory = null;
		if (changed)
		{
			potHistory = (string)(dictionary["pot_history"] = JsonSerializer.Serialize(new UploadPot[2] { uploadPot, uploadPot2 }));
		}
		using StringContent content = new StringContent(JsonSerializer.Serialize(dictionary), Encoding.UTF8, "application/json");
		(await Client.PatchAsync($"{"https://infi.ovh/api/"}{"OccultTrackerV3"}?id=eq.{row.RowID}", content)).EnsureSuccessStatusCode();
		row.LastFateHash = context.Fingerprint;
		row.Fate = context.FateID;
		row.FateTimestamp = context.FateTimestamp;
		row.LastUpdate = now;
		if (potHistory != null)
		{
			row.PotHistory = potHistory;
		}
	}

	private async Task<TrackerRow?> CreateRowAsync(SyncContext context, long now)
	{
		string value = JsonSerializer.Serialize(new UploadPot[2]
		{
			UploadPot.From(context.NorthFateID, context.North),
			UploadPot.From(context.SouthFateID, context.South)
		});
		string content = JsonSerializer.Serialize(new Dictionary<string, object>
		{
			["version"] = "OccultPot-MagicPot",
			["territory"] = context.Territory,
			["last_fate"] = context.Fingerprint,
			["tracker_type"] = 1,
			["datacenter"] = context.Datacenter,
			["fate"] = context.FateID,
			["fate_timestamp"] = context.FateTimestamp,
			["encounter_history"] = "[]",
			["fate_history"] = "[]",
			["pot_history"] = value,
			["last_update"] = now
		});
		using StringContent content2 = new StringContent(content, Encoding.UTF8, "application/json");
		HttpResponseMessage obj = await Client.PostAsync("https://infi.ovh/api/OccultTrackerV3", content2);
		obj.EnsureSuccessStatusCode();
		TrackerRow[] array = JsonSerializer.Deserialize<TrackerRow[]>(await obj.Content.ReadAsStringAsync(), JSONOptions);
		return (array != null && array.Length > 0) ? array[0] : null;
	}

	private static UploadPot MergePot(uint fateID, PotObs local, SharedPot[]? shared, ref bool changed)
	{
		long spawnTime = -1L;
		long deathTime = 0L;
		long num = -1L;
		if (shared != null)
		{
			foreach (SharedPot sharedPot in shared)
			{
				if (sharedPot.FateID == fateID)
				{
					spawnTime = sharedPot.SpawnTime;
					deathTime = sharedPot.DeathTime;
					num = sharedPot.LastSeen;
					break;
				}
			}
		}
		if (local.Observed && local.LastSeen > num)
		{
			spawnTime = local.Spawn;
			deathTime = local.Death;
			num = local.LastSeen;
			changed = true;
		}
		return new UploadPot
		{
			FateID = fateID,
			SpawnTime = spawnTime,
			DeathTime = deathTime,
			LastSeen = num
		};
	}

	private void ApplyPendingSync()
	{
		(uint, long, long, long, long)? tuple;
		lock (syncLock)
		{
			tuple = pendingSync;
			pendingSync = null;
		}
		if (tuple.HasValue && tuple.Value.Item1 == GameState.TerritoryType && TryGetCurrentPots((ushort)tuple.Value.Item1, out PotState north, out PotState south))
		{
			MergeSynced(north, tuple.Value.Item2, tuple.Value.Item3);
			MergeSynced(south, tuple.Value.Item4, tuple.Value.Item5);
		}
	}

	private void TrySyncCatalog(long now)
	{
		if (!catalogInFlight)
		{
			int num = ((hasCatalog && !catalogFetchFailed) ? 60 : 5);
			if (lastCatalogAt <= 0 || now - lastCatalogAt >= num)
			{
				catalogInFlight = true;
				FetchCatalogAsync(now);
			}
		}
	}

	private async Task FetchCatalogAsync(long now)
	{
		bool ok = false;
		try
		{
			long minUpdate = now - 14400;
			string requestURI = $"{"https://infi.ovh/api/"}{"OccultTrackerV3"}?tracker_type=eq.1&territory=in.({1252},{1346})&last_update=gte.{minUpdate}&select=id,territory,datacenter,server,last_update,pot_history" + "&order=last_update.desc&limit=120";
			TrackerRow[] array = JsonSerializer.Deserialize<TrackerRow[]>(await Client.GetStringAsync(requestURI), JSONOptions);
			if (array == null || array.Length == 0)
			{
				requestURI = $"{"https://infi.ovh/api/"}{"OccultTrackerV3"}?territory=in.({1252},{1346})&last_update=gte.{minUpdate}&select=id,territory,datacenter,server,last_update,pot_history" + "&order=last_update.desc&limit=120";
				array = JsonSerializer.Deserialize<TrackerRow[]>(await Client.GetStringAsync(requestURI), JSONOptions);
			}
			if (array == null || array.Length == 0)
			{
				return;
			}
			Dictionary<(CnDataCenterKind, ushort), RemoteIslandSnapshot> dictionary = new Dictionary<(CnDataCenterKind, ushort), RemoteIslandSnapshot>();
			TrackerRow[] array2 = array;
			foreach (TrackerRow trackerRow in array2)
			{
				uint territory = trackerRow.Territory;
				if ((territory != 1252 && territory != 1346) || 1 == 0)
				{
					continue;
				}
				CnDataCenterKind? cnDataCenterKind = ResolveRowDC(trackerRow);
				if (cnDataCenterKind.HasValue)
				{
					(CnDataCenterKind, ushort) key = (cnDataCenterKind.Value, (ushort)trackerRow.Territory);
					if (!dictionary.TryGetValue(key, out var value) || value.LastUpdate < trackerRow.LastUpdate)
					{
						ParseHistory(trackerRow, (ushort)trackerRow.Territory, out var northSpawn, out var southSpawn, out var northDeath, out var southDeath);
						dictionary[key] = new RemoteIslandSnapshot(cnDataCenterKind.Value, (ushort)trackerRow.Territory, trackerRow.LastUpdate, northSpawn, southSpawn, northDeath, southDeath);
					}
				}
			}
			if (dictionary.Count != 0)
			{
				lock (syncLock)
				{
					pendingCatalog = dictionary.Values.ToList();
				}
				ok = true;
			}
		}
		catch
		{
		}
		finally
		{
			lastCatalogAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			catalogFetchFailed = !ok;
			catalogInFlight = false;
		}
	}

	private static CnDataCenterKind? ResolveRowDC(TrackerRow row)
	{
		if (row.Datacenter != 0)
		{
			return CnWorldCatalog.KindForDataCenterID(row.Datacenter);
		}
		if (row.Server != 0)
		{
			return CnWorldCatalog.KindForWorldID(row.Server);
		}
		return null;
	}

	private static void ParseHistory(TrackerRow row, ushort territory, out long northSpawn, out long southSpawn, out long northDeath, out long southDeath)
	{
		northSpawn = -1L;
		southSpawn = -1L;
		northDeath = 0L;
		southDeath = 0L;
		uint num = IslandPotLayout.North(territory)?.FateID ?? 0;
		uint num2 = IslandPotLayout.South(territory)?.FateID ?? 0;
		SharedPot[] array = ParseSharedPotHistory(row);
		if (array == null)
		{
			return;
		}
		SharedPot[] array2 = array;
		foreach (SharedPot sharedPot in array2)
		{
			if (sharedPot.FateID == num)
			{
				northSpawn = sharedPot.SpawnTime;
				northDeath = sharedPot.DeathTime;
			}
			else if (sharedPot.FateID == num2)
			{
				southSpawn = sharedPot.SpawnTime;
				southDeath = sharedPot.DeathTime;
			}
		}
	}

	private void ApplyPendingCatalog()
	{
		List<RemoteIslandSnapshot> list;
		lock (syncLock)
		{
			list = pendingCatalog;
			pendingCatalog = null;
		}
		if (list == null)
		{
			return;
		}
		lock (syncLock)
		{
			foreach (RemoteIslandSnapshot item in list)
			{
				remote[(item.DC, item.Territory)] = item;
			}
		}
		hasCatalog = true;
		catalogFetchFailed = false;
	}

	private void MergeLocalIntoCatalog(long now)
	{
		ushort num = (ushort)GameState.TerritoryType;
		if (!ZoneIds.IsSupportedIsland(num) || !TryGetCurrentPots(num, out PotState north, out PotState south))
		{
			return;
		}
		CnDataCenterKind? cnDataCenterKind = CnWorldCatalog.KindForWorldID(CnWorldCatalog.CurrentWorldID) ?? CnWorldCatalog.KindForDataCenterID(GameState.CurrentDataCenter);
		if (!cnDataCenterKind.HasValue)
		{
			return;
		}
		lock (syncLock)
		{
			remote.TryGetValue((cnDataCenterKind.Value, num), out var value);
			long num2 = MergeSpawn(north, value.NorthSpawn);
			long num3 = MergeSpawn(south, value.SouthSpawn);
			if (num2 <= 0 && num3 <= 0)
			{
				return;
			}
			remote[(cnDataCenterKind.Value, num)] = new RemoteIslandSnapshot(cnDataCenterKind.Value, num, now, num2, num3, MergeDeath(north, value.NorthDeath), MergeDeath(south, value.SouthDeath));
		}
		hasCatalog = true;
	}

	private static long MergeSpawn(PotState local, long catalog)
	{
		if (local.LocallyObserved && local.SpawnTime > 0)
		{
			return local.SpawnTime;
		}
		if (catalog <= 0)
		{
			return 0L;
		}
		return catalog;
	}

	private static long MergeDeath(PotState local, long catalog)
	{
		if (local.LocallyObserved)
		{
			return local.DeathTime;
		}
		return catalog;
	}

	private bool TryCatalogCompute(ushort territory, long now, out PotKind kind, out int wait, out int untilGone, out bool alive)
	{
		kind = PotKind.North;
		wait = int.MaxValue;
		untilGone = 0;
		alive = false;
		CnDataCenterKind? cnDataCenterKind = CnWorldCatalog.KindForWorldID(CnWorldCatalog.CurrentWorldID) ?? CnWorldCatalog.KindForDataCenterID(GameState.CurrentDataCenter);
		if (!cnDataCenterKind.HasValue)
		{
			return false;
		}
		RemoteIslandSnapshot value;
		lock (syncLock)
		{
			if (!remote.TryGetValue((cnDataCenterKind.Value, territory), out value))
			{
				return false;
			}
		}
		return OccultTrackerPlanner.TryComputeNext(value, now, out kind, out wait, out untilGone, out alive);
	}

	private bool TryLocalCompute(ushort territory, long now, out PotKind kind, out int wait, out int untilGone, out bool alive)
	{
		kind = PotKind.North;
		wait = int.MaxValue;
		untilGone = 0;
		alive = false;
		if (!TryGetCurrentPots(territory, out PotState north, out PotState south))
		{
			return false;
		}
		if (north.SpawnTime <= 0 && south.SpawnTime <= 0)
		{
			return false;
		}
		CnDataCenterKind? cnDataCenterKind = CnWorldCatalog.KindForWorldID(CnWorldCatalog.CurrentWorldID) ?? CnWorldCatalog.KindForDataCenterID(GameState.CurrentDataCenter);
		if (!cnDataCenterKind.HasValue)
		{
			return false;
		}
		return OccultTrackerPlanner.TryComputeNext(new RemoteIslandSnapshot(cnDataCenterKind.Value, territory, now, (north.SpawnTime > 0) ? north.SpawnTime : 0, (south.SpawnTime > 0) ? south.SpawnTime : 0, north.DeathTime, south.DeathTime), now, out kind, out wait, out untilGone, out alive);
	}

	private bool LocalPredictionTrusted(ushort territory, long now)
	{
		if (!TryGetCurrentPots(territory, out PotState north, out PotState south))
		{
			return false;
		}
		if (north.Alive || south.Alive)
		{
			return true;
		}
		if (north.SpawnTime > 0 && south.SpawnTime > 0)
		{
			return true;
		}
		if (!north.LocallyObserved && !south.LocallyObserved)
		{
			return false;
		}
		long num = Math.Max(north.SpawnTime, south.SpawnTime);
		if (num > 0)
		{
			return now - num < 1800;
		}
		return false;
	}

	private void UpdateCatalogStatus(long now)
	{
		if (!hasCatalog)
		{
			CatalogStatus = RuntimeStatus.Of(RuntimeStatusCode.Tracker_CatalogWaiting);
			return;
		}
		List<string> list = new List<string>(4);
		lock (syncLock)
		{
			(CnDataCenterKind, string, string)[] all = CnWorldCatalog.All;
			for (int i = 0; i < all.Length; i++)
			{
				CnDataCenterKind item = all[i].Item1;
				remote.TryGetValue((item, 1252), out var value);
				remote.TryGetValue((item, 1346), out var value2);
				list.Add($"{CnWorldCatalog.DCDisplayName(item)} 南征 {OccultTrackerPlanner.FormatIsland((value.LastUpdate == 0L) ? ((RemoteIslandSnapshot?)null) : new RemoteIslandSnapshot?(value), now)} 北征 {OccultTrackerPlanner.FormatIsland((value2.LastUpdate == 0L) ? ((RemoteIslandSnapshot?)null) : new RemoteIslandSnapshot?(value2), now)}");
			}
		}
		CatalogStatus = RuntimeStatus.Literal(string.Join(" | ", list));
	}

	private static void MergeSynced(PotState pot, long spawn, long lastSeen)
	{
		if (!pot.Alive)
		{
			if (lastSeen > pot.LastSeenAlive)
			{
				pot.LastSeenAlive = lastSeen;
			}
			if (spawn > pot.SpawnTime)
			{
				pot.SpawnTime = spawn;
			}
		}
	}

	private void UpdateStatus(long now)
	{
		ushort territory = (ushort)GameState.TerritoryType;
		if (!TryGetTarget(territory, out PotKind _, out string reason))
		{
			statusLine = RuntimeStatus.Of(hasOnlineData
				? RuntimeStatusCode.Tracker_OnlineWaitingPots
				: RuntimeStatusCode.Tracker_OnlineWaitingFingerprint);
		}
		else
		{
			statusLine = RuntimeStatus.Of
			(
				hasOnlineData ? RuntimeStatusCode.Tracker_OnlineDetail : RuntimeStatusCode.Tracker_LocalDetail,
				reason
			);
		}
	}

	private bool TryGetCurrentPots(ushort territory, out PotState north, out PotState south)
	{
		north = null;
		south = null;
		PotState[] array = pots;
		foreach (PotState potState in array)
		{
			if (potState.TerritoryID == territory)
			{
				if (potState.Kind == PotKind.North)
				{
					north = potState;
				}
				else
				{
					south = potState;
				}
			}
		}
		if (north != null)
		{
			return south != null;
		}
		return false;
	}

	private PotState? GetPot(uint fateID)
	{
		PotState[] array = pots;
		foreach (PotState potState in array)
		{
			if (potState.FateID == fateID)
			{
				return potState;
			}
		}
		return null;
	}

	private static string ComputeHash(uint dcID, uint fateID, int timestamp)
	{
		Span<byte> span = stackalloc byte[12];
		BitConverter.TryWriteBytes(span.Slice(0, 4), dcID);
		BitConverter.TryWriteBytes(span.Slice(4, 4), fateID);
		BitConverter.TryWriteBytes(span.Slice(8), timestamp);
		Span<byte> span2 = stackalloc byte[32];
		SHA256.HashData(span, span2);
		StringBuilder stringBuilder = new StringBuilder(64);
		Span<byte> span3 = span2;
		for (int i = 0; i < span3.Length; i++)
		{
			byte b = span3[i];
			stringBuilder.Append(b.ToString("X2"));
		}
		return stringBuilder.ToString();
	}

	private static HttpClient CreateClient()
	{
		HttpClient httpClient = new HttpClient();
		httpClient.Timeout = TimeSpan.FromSeconds(15L);
		httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJyb2xlIjoiYW5vbiJ9.Ur6wgi_rD4dr3uLLvbLoaEvfLCu4QFWdrF-uHRtbl_s");
		httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Prefer", "return=representation, resolution=ignore-duplicates, on_conflict=last_fate");
		httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "OccultPot-MagicPot");
		return httpClient;
	}
}
