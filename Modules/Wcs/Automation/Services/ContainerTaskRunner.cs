using GrcsBackend.Modules.Wcs.Proxy.Services;
using System.Text.Json;
using GrcsBackend.Modules.Wcs.Automation.Models;
using GrcsBackend.Modules.Wcs.Console.Services;

namespace GrcsBackend.Modules.Wcs.Automation.Services;

/// <summary>
/// 批量容器任务执行器（由前端 ContainerTaskService 平移而来）。
/// 一次性批量执行两段式任务（入库/出库/分拣），消费库存快照 + 选点范围，
/// 与 AutoRunHostedService 共享同一套后端存储（锁/货物码/台账/日志）。
/// 单例注册；控制器 POST /auto/container/execute 触发。
/// </summary>
public class ContainerTaskRunner
{
    private readonly GrcsHttpClient _grcs;
    private readonly MapStoreService _mapStore;
    private readonly RangeConfigService _rangeConfig;
    private readonly WcsSettingsService _settings;
    private readonly StationLockStore _locks;
    private readonly CargoCodeStore _cargoCodes;
    private readonly LedgerStore _ledger;
    private readonly AutomationLogService _logs;
    private readonly ITaskStageService _stages;
    private readonly AutomationGate _gate;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public bool Busy { get; private set; }
    public int Done { get; private set; }
    public int Total { get; private set; }
    public string Status { get; private set; } = "";

    // ── 库存快照（执行前查询；与自动化共享同一口径：范围内储位 + ToMark 归一化）──
    private readonly List<(string Code, string Station)> _cachedEmptyPallets = [];
    private readonly List<(string Code, string Station)> _cachedLoadedPallets = [];
    private readonly List<(string Code, string Station)> _cachedPairedCargos = [];
    public InventoryCountsDto Inventory { get; } = new();

    public ContainerTaskRunner(GrcsHttpClient grcs, MapStoreService mapStore, RangeConfigService rangeConfig,
        WcsSettingsService settings, StationLockStore locks, CargoCodeStore cargoCodes, LedgerStore ledger,
        AutomationLogService logs, ITaskStageService stages, AutomationGate gate)
    {
        _grcs = grcs;
        _mapStore = mapStore;
        _rangeConfig = rangeConfig;
        _settings = settings;
        _locks = locks;
        _cargoCodes = cargoCodes;
        _ledger = ledger;
        _logs = logs;
        _stages = stages;
        _gate = gate;
    }

    /// <summary>查询库存并缓存（控制器「查询库存」按钮直接调用）。返回状态文本。</summary>
    public async Task<string> RefreshInventoryAsync()
    {
        var settings = _settings.Get();
        var mapStations = _mapStore.GetStations();
        if (string.IsNullOrEmpty(settings.SceneName)) { var m = "❌ 请先配置场景名称（连接设置）"; _logs.Add(m, "#f87171"); return m; }
        if (mapStations.Count == 0) { var m = "❌ 地图站点未上传，请先在「地图信息」页上传"; _logs.Add(m, "#f87171"); return m; }
        var range = _rangeConfig.Get();

        // 与 AutoRunHostedService 同口径：只认（范围内）储位上的库存；站点码 ToMark 归一化
        var storages = mapStations.Where(s => s.StaEnable && (s.StationType & MapStationTypeBits.StorageLocation) != 0).ToList();
        if (range.Enabled)
        {
            var pool = range.ApplyTo(mapStations);
            storages.RemoveAll(s => !pool.Contains(s));
        }
        var storageMarks = storages.Select(s => s.Mark).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _logs.Add("📋 查询库存中...", "#94a3b8");
        var allPallets = new List<(string Code, string Station)>();
        var allCargos = new List<(string Code, string Station)>();
        var resultMsg = "";
        try
        {
            var (ok, _, json) = await _grcs.QueryCargoInventoryAsync(settings.GrcsBaseUrl, settings.SceneName);
            if (ok)
            {
                var result = JsonSerializer.Deserialize<CargoQueryResult>(json, Opts);
                if (result?.Data?.Records != null)
                {
                    int locked = 0, loaded = 0, nocode = 0;
                    foreach (var c in result.Data.Records)
                    {
                        if (c.IsLocked) { locked++; continue; }
                        if (c.IsLoaded) { loaded++; continue; }
                        var loc = c.CurrentStationCode;
                        if (string.IsNullOrEmpty(c.Code) || string.IsNullOrEmpty(loc)) { nocode++; continue; }
                        var stationCode = loc;
                        if (stationCode.Length > 2 && (stationCode[^2..] is "_0" or "_1")) stationCode = stationCode[..^2];
                        if (!storageMarks.Contains(stationCode)) continue;
                        if (c.IsPallet()) allPallets.Add((c.Code, stationCode));
                        else if (c.IsCargo()) allCargos.Add((c.Code, stationCode));
                    }
                    resultMsg = "总数 " + result.Data.Records.Count + " ";
                    if (locked > 0 || loaded > 0 || nocode > 0)
                        resultMsg += "跳过:" + locked + "锁/" + loaded + "途/" + nocode + "码 ";
                    resultMsg += "→ ";
                }
            }
        }
        catch (Exception ex) { _logs.Add("❌ 库存查询异常：" + ex.Message, "#f87171"); }

        var cargoMarks = new HashSet<string>(allCargos.Select(c => c.Station), StringComparer.OrdinalIgnoreCase);
        var palletMarks = new HashSet<string>(allPallets.Select(p => p.Station), StringComparer.OrdinalIgnoreCase);
        _cachedEmptyPallets.Clear(); _cachedEmptyPallets.AddRange(allPallets.Where(p => !cargoMarks.Contains(p.Station)));
        _cachedLoadedPallets.Clear(); _cachedLoadedPallets.AddRange(allPallets.Where(p => cargoMarks.Contains(p.Station)));
        _cachedPairedCargos.Clear(); _cachedPairedCargos.AddRange(allCargos.Where(c => palletMarks.Contains(c.Station)));

        Inventory.EmptyPallets = _cachedEmptyPallets.Count;
        Inventory.LoadedPallets = _cachedLoadedPallets.Count;
        Inventory.Cargos = allCargos.Count(c => !palletMarks.Contains(c.Station));
        Inventory.PairedCargos = _cachedPairedCargos.Count;

        var scopeText = range.Enabled ? "范围内" : "全图";
        var text = resultMsg + scopeText + " 空托 " + Inventory.EmptyPallets + " / 带货托 " + Inventory.LoadedPallets + " / 货物 " + Inventory.Cargos + " / 任务可用货物 " + Inventory.PairedCargos;
        Status = text;
        _logs.Add("库存查询完成（" + scopeText + "）：" + text, "#4ade80");
        return text;
    }

    /// <summary>执行批量两段式容器任务（平移前端 ExecuteAsync）。返回结果文本。</summary>
    public async Task<string> ExecuteAsync(int flow, int count, int interval, string? tabId = null)
    {
        if (Busy) return "⚠️ 批量任务正在执行中，请等待完成";
        if (!_gate.TryStartBatch(tabId))
        {
            var m = "❌ 无法执行批量任务：轮询自动化或另一标签页的移动循环正在运行中（互斥）";
            _logs.Add(m, "#f87171");
            return m;
        }
        try
        {
            return await ExecuteCoreAsync(flow, count, interval);
        }
        finally
        {
            _gate.StopBatch();
        }
    }

    private async Task<string> ExecuteCoreAsync(int flow, int count, int interval)
    {
        if (Busy) return "⚠️ 批量任务正在执行中，请等待完成";
        var settings = _settings.Get();
        var mapStations = _mapStore.GetStations();
        if (string.IsNullOrEmpty(settings.SceneName)) { var m = "❌ 请先配置场景名称（连接设置）"; _logs.Add(m, "#f87171"); return m; }
        if (mapStations.Count == 0) { var m = "❌ 地图站点未上传"; _logs.Add(m, "#f87171"); return m; }

        if (_cachedEmptyPallets.Count == 0 && _cachedLoadedPallets.Count == 0 && _cachedPairedCargos.Count == 0)
            await RefreshInventoryAsync();

        var flowName = flow switch { 1 => "空托盘入库", 2 => "带货托盘出库", _ => "带货托盘分拣" };
        _logs.Add("🚀 开始执行：" + flowName + " × " + count + "（间隔 " + interval + " s），空托 " + Inventory.EmptyPallets + " / 带货托 " + Inventory.LoadedPallets + " / 任务可用货物 " + Inventory.PairedCargos, "#60a5fa");

        Busy = true; Done = 0; Total = count; Status = "";
        var rand = Random.Shared;
        int okCount = 0; var errors = new List<string>();
        var ctaTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x").ToUpper();
        var range = _rangeConfig.Get();

        var storages = mapStations.Where(s => s.StaEnable && (s.StationType & MapStationTypeBits.StorageLocation) != 0).ToList();
        var transferPoints = mapStations.Where(s => s.StaEnable && (s.StationType & MapStationTypeBits.TransferPoint) != 0).ToList();
        var pickingStations = mapStations.Where(s => s.StaEnable && (s.StationType & MapStationTypeBits.PeopleStation) != 0).ToList();
        if (range.Enabled)
        {
            var pool = range.ApplyTo(mapStations);
            storages.RemoveAll(s => !pool.Contains(s));
            transferPoints.RemoveAll(s => !pool.Contains(s));
            pickingStations.RemoveAll(s => !pool.Contains(s));
        }
        var lockedStations = _locks.GetLocked(_stages);
        var occupiedMarks = new HashSet<string>(_cachedEmptyPallets.Select(p => p.Station).Concat(_cachedLoadedPallets.Select(p => p.Station)).Concat(_cachedPairedCargos.Select(c => c.Station)), StringComparer.OrdinalIgnoreCase);
        var emptyStorages = storages.Where(s => !occupiedMarks.Contains(s.Mark) && !lockedStations.Contains(s.Mark)).ToList();

        var emptyPallets = _cachedEmptyPallets.ToList();
        var loadedPallets = _cachedLoadedPallets.ToList();
        var pairedCargos = _cachedPairedCargos.ToList();

        var seg2s = new List<(int No, string Id1, string Id2, string Type2, string? CC2, List<string> Sta2, string Seg2Pallet, string Seg2Cargo)>();
        string seg1PalletCode = "";
        for (int i = 0; i < count; i++)
        {
            try
            {
                string taskType1, taskType2 = null!;
                List<string> stations1, stations2 = null!;
                string cc1; string? cc2 = null;

                if (flow == 1)
                {
                    if (emptyPallets.Count == 0) { var m2 = "#" + (i + 1) + " 无可用空托盘"; errors.Add(m2); _logs.Add("⚠ " + m2, "#fbbf24"); continue; }
                    if (transferPoints.Count == 0) { var m2 = "#" + (i + 1) + " 无可用接驳位"; errors.Add(m2); _logs.Add("⚠ " + m2, "#fbbf24"); continue; }
                    if (emptyStorages.Count == 0) { var m2 = "#" + (i + 1) + " 无空货位"; errors.Add(m2); _logs.Add("⚠ " + m2, "#fbbf24"); continue; }
                    var pallet = emptyPallets[rand.Next(emptyPallets.Count)];
                    var tpSt = transferPoints[rand.Next(transferPoints.Count)];
                    var dstSt = emptyStorages[rand.Next(emptyStorages.Count)];
                    emptyStorages.Remove(dstSt);
                    var srcSt = storages.FirstOrDefault(s => s.Mark == pallet.Station);
                    taskType1 = "CONTAINER_CARRY_INBOUND";
                    stations1 = [srcSt?.ToWcsCode() ?? pallet.Station, tpSt.ToWcsCode()];
                    taskType2 = "CARGO_CARRY_INBOUND";
                    stations2 = [tpSt.ToWcsCode(), dstSt.ToWcsCode()];
                    cc1 = pallet.Code; cc2 = null;
                    _locks.Acquire(dstSt.Mark, "SimAuto_" + ctaTs + "_" + i + "b");
                    emptyPallets.Remove(pallet);
                }
                else if (flow == 2)
                {
                    if (loadedPallets.Count == 0) { var m2 = "#" + (i + 1) + " 无可用带货托盘"; errors.Add(m2); _logs.Add("⚠ " + m2, "#fbbf24"); continue; }
                    if (transferPoints.Count == 0) { var m2 = "#" + (i + 1) + " 无可用接驳位"; errors.Add(m2); _logs.Add("⚠ " + m2, "#fbbf24"); continue; }
                    if (emptyStorages.Count == 0) { var m2 = "#" + (i + 1) + " 无空货位"; errors.Add(m2); _logs.Add("⚠ " + m2, "#fbbf24"); continue; }
                    var loaded = loadedPallets[rand.Next(loadedPallets.Count)];
                    var cargo = pairedCargos.FirstOrDefault(c => c.Station == loaded.Station);
                    if (cargo.Code == null) { var m2 = "#" + (i + 1) + " 带货托盘 " + loaded.Code + " 无对应货物"; errors.Add(m2); _logs.Add("⚠ " + m2, "#fbbf24"); continue; }
                    var tpSt = transferPoints[rand.Next(transferPoints.Count)];
                    var dstSt = emptyStorages[rand.Next(emptyStorages.Count)];
                    emptyStorages.Remove(dstSt);
                    var srcSt = storages.FirstOrDefault(s => s.Mark == loaded.Station);
                    taskType1 = "CARGO_CARRY_OUTBOUND";
                    stations1 = [srcSt?.ToWcsCode() ?? loaded.Station, tpSt.ToWcsCode()];
                    taskType2 = "CONTAINER_CARRY_OUTBOUND";
                    stations2 = [tpSt.ToWcsCode(), dstSt.ToWcsCode()];
                    cc1 = cargo.Code; cc2 = loaded.Code;
                    _locks.Acquire(dstSt.Mark, "SimAuto_" + ctaTs + "_" + i + "b");
                    loadedPallets.Remove(loaded);
                    pairedCargos.Remove(cargo);
                }
                else
                {
                    if (pairedCargos.Count == 0) { var m2 = "#" + (i + 1) + " 无可用任务货物"; errors.Add(m2); _logs.Add("⚠ " + m2, "#fbbf24"); continue; }
                    if (pickingStations.Count == 0) { var m2 = "#" + (i + 1) + " 无人工分拣台"; errors.Add(m2); _logs.Add("⚠ " + m2, "#fbbf24"); continue; }
                    var cargo = pairedCargos[rand.Next(pairedCargos.Count)];
                    var pickSt = pickingStations[rand.Next(pickingStations.Count)];
                    var srcSt = storages.FirstOrDefault(s => s.Mark == cargo.Station);
                    taskType1 = "SORTING";
                    stations1 = [srcSt?.ToWcsCode() ?? cargo.Station, pickSt.ToWcsCode()];
                    cc1 = cargo.Code;
                    seg1PalletCode = _cachedLoadedPallets.FirstOrDefault(p => p.Station == cargo.Station).Code ?? "";
                    pairedCargos.Remove(cargo);
                }

                var id1 = "SimAuto_" + ctaTs + "_" + i + "a";
                _logs.Add("#" + (i + 1) + " 段1 " + id1 + " " + taskType1 + " " + cc1 + " → " + string.Join("→", stations1), "#60a5fa");
                var payload1 = new WcsTaskGroup
                {
                    GroupId = "G_" + id1, MsgTime = DateTime.Now.ToString("O"), PriorityCode = 50, Warehouse = settings.SceneName,
                    Tasks = [new WcsTaskItem { TaskId = id1, TaskType = taskType1, ContainerCode = cc1, StationCode = stations1 }]
                };
                var (ok1, code1, _) = await _grcs.SendTaskGroupAsync(settings.GrcsBaseUrl, payload1);
                var seg1Pallet = taskType1 == "CONTAINER_CARRY_INBOUND" ? cc1 : (taskType1 == "CARGO_CARRY_OUTBOUND" ? cc2 ?? "" : seg1PalletCode);
                var seg1Cargo = taskType1 == "CONTAINER_CARRY_INBOUND" ? "" : cc1;
                await _ledger.AppendAsync([new TaskLedgerEntry { TaskId = id1, TaskType = taskType1, ContainerCode = seg1Pallet, CargoCode = seg1Cargo, StationCode = stations1, Warehouse = settings.SceneName, Time = DateTime.Now.ToString("O"), Ok = ok1, StatusCode = code1 }]);
                if (!ok1) { var m2 = "#" + (i + 1) + " 段1 下发失败 HTTP" + code1; errors.Add(m2); _logs.Add("❌ " + m2, "#f87171"); continue; }

                var id2 = "SimAuto_" + ctaTs + "_" + i + "b";
                string seg2Pallet, seg2Cargo;
                if (flow == 1) { seg2Pallet = cc1; seg2Cargo = ""; }
                else if (flow == 2) { seg2Pallet = cc2 ?? ""; seg2Cargo = cc1; }
                else { seg2Pallet = seg1PalletCode; seg2Cargo = cc1; }
                seg2s.Add((i + 1, id1, id2, taskType2, cc2, stations2, seg2Pallet, seg2Cargo));
                Done++;
                Status = "段1 已下发 " + Done + "/" + Total;

                if (interval > 0 && i < count - 1)
                    await Task.Delay(interval * 1000);
            }
            catch (Exception ex) { var m2 = "#" + (i + 1) + " 异常: " + ex.Message; errors.Add(m2); _logs.Add("❌ " + m2, "#f87171"); }
        }

        // ── 阶段2：并行等待段1 FINISHED，谁先完成谁立刻下发段2 ──
        var lockObj = new object();
        await Task.WhenAll(seg2s.Select(async seg2 =>
        {
            var (no, id1, id2, type2, cc2, sta2, seg2Pallet, seg2Cargo) = seg2;
            try
            {
                if (type2 == null) { lock (lockObj) { okCount++; } _logs.Add("#" + no + " 分拣流程无段2，跳过", "#94a3b8"); return; }
                _logs.Add("#" + no + " 等待段1 完成 " + id1 + " ...", "#fbbf24");
                await _stages.WaitFinishedAsync(id1);
                _logs.Add("#" + no + " 段1 " + id1 + " 完成", "#4ade80");

                var cc2Final = (type2 == "CARGO_CARRY_INBOUND" ? _cargoCodes.Ensure(id1) : cc2)!;
                _logs.Add("#" + no + " 段2 " + id2 + " " + type2 + " " + cc2Final + " → " + string.Join("→", sta2), "#60a5fa");
                var payload2 = new WcsTaskGroup
                {
                    GroupId = "G_" + id2, MsgTime = DateTime.Now.ToString("O"), PriorityCode = 50, Warehouse = settings.SceneName,
                    Tasks = [new WcsTaskItem { TaskId = id2, TaskType = type2, ContainerCode = cc2Final, StationCode = sta2 }]
                };
                var (ok2, code2, _) = await _grcs.SendTaskGroupAsync(settings.GrcsBaseUrl, payload2);
                var seg2CargoFinal = type2 == "CARGO_CARRY_INBOUND" ? cc2Final : seg2Cargo;
                await _ledger.AppendAsync([new TaskLedgerEntry { TaskId = id2, TaskType = type2, ContainerCode = seg2Pallet, CargoCode = seg2CargoFinal, StationCode = sta2, Warehouse = settings.SceneName, Time = DateTime.Now.ToString("O"), Ok = ok2, StatusCode = code2 }]);
                lock (lockObj)
                {
                    if (!ok2) { var m2 = "#" + no + " 段2 " + type2 + " 下发失败 HTTP" + code2; errors.Add(m2); _logs.Add("❌ " + m2, "#f87171"); }
                    else { okCount++; _logs.Add("✓ #" + no + " 段2 已下发 " + id2, "#4ade80"); }
                }
            }
            catch (Exception ex) { var m2 = "#" + no + " 段2 异常: " + ex.Message; lock (lockObj) { errors.Add(m2); } _logs.Add("❌ " + m2, "#f87171"); }
        }));

        Busy = false;
        var finalMsg = "完成 " + okCount + "/" + Total + (errors.Count > 0 ? " / " + errors.Count + " 个失败" : "");
        Status = finalMsg;
        _logs.Add((errors.Count == 0 ? "✅ " : "⚠️ ") + finalMsg, errors.Count == 0 ? "#4ade80" : "#fbbf24");
        return (errors.Count == 0 ? "✅" : "⚠️") + " 自动容器任务 " + finalMsg
            + (errors.Count > 0 ? "\n\n" + string.Join("\n", errors.Take(10)) : "");
    }
}
