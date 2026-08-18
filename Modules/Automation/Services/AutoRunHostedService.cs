using System.Text.Json;
using GrcsBackend.Modules.Automation.Models;
using GrcsBackend.Modules.Wcs.Services;

namespace GrcsBackend.Modules.Automation.Services;

/// <summary>
/// 自动化轮询执行服务（由前端 AutoRunService 平移而来，Skill E 核心）。
/// 常驻后台（IHostedService + PeriodicTimer）：按 Interval 秒轮询 GRCS 库存，
/// 在选点范围内发现空托 → 下发入库，带货托按 FlowMode（交替/只分拣/只出库/无任务）下发。
/// 与前端版差异：
///  - GRCS 地址/场景名从 WcsSettingsService 取（前端「连接设置」PUT 到后端）；
///  - 段1 FINISHED 等待改为进程内订阅 TaskStageService 事件（不再轮询 HTTP）；
///  - 锁/货物码/台账/日志全部后端化（StationLockStore/CargoCodeStore/LedgerStore/AutomationLogService）。
/// 单例注册：控制器直接注入操纵启停与参数。
/// </summary>
public class AutoRunHostedService : IHostedService, IDisposable
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
    private CancellationTokenSource? _cts;

    // ── 状态（GET /auto/status 快照）──
    public bool Running { get; private set; }
    public int Interval { get; set; } = 5;
    public int FlowMode { get; set; }   // 0=出库/分拣交替 1=只分拣 2=只出库 3=无任务
    public int Dispatched { get; private set; }
    public string Status { get; private set; } = "";

    // ── 库存快照（最近一轮查询结果）──
    public InventoryCountsDto Inventory { get; } = new();

    private readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);
    private int _seq;
    private int _outboundTurn;
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public AutoRunHostedService(GrcsHttpClient grcs, MapStoreService mapStore, RangeConfigService rangeConfig,
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 不在宿主启动时自动运行：由前端 POST /auto/start 拉起（冷启动默认停）
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    /// <summary>启动轮询自动化（互斥闸：批量执行或移动循环中拒绝）。返回是否成功。</summary>
    public bool Start(string? tabId = null)
    {
        lock (_lock)
        {
            if (Running) return true;
            if (!_gate.TryStartAuto(tabId))
            {
                _logs.Add("❌ 无法启动轮询自动化：批量任务执行中或另一标签页正在下发移动任务（互斥）", "#f87171");
                return false;
            }
            Running = true;
            _cts = new CancellationTokenSource();
        }
        _logs.Add("▶ 自动化启动，轮询间隔 " + Interval + " 秒", "#4ade80");
        _ = PollLoopAsync();
        return true;
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!Running) return;
            Running = false;
            _gate.StopAuto();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
        _logs.Add("⏹ 自动化已停止", "#fbbf24");
    }

    public void ClearHandled() { lock (_lock) { _handled.Clear(); } }

    private async Task PollLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, Interval)));
        while (Running)
        {
            try { await PollOnceAsync(); }
            catch (Exception ex) { _logs.Add("❌ 轮询异常: " + ex.Message, "#f87171"); }
            try { if (!await timer.WaitForNextTickAsync()) break; } catch { break; }
        }
    }

    /// <summary>平移前端 AutoRunService.PollOnceAsync：查库存 → 范围过滤（ToMark 归一化 + 只认范围内储位）→ 配对 → 分发。</summary>
    private async Task PollOnceAsync()
    {
        var settings = _settings.Get();
        var mapStations = _mapStore.GetStations();
        if (string.IsNullOrEmpty(settings.SceneName)) { _logs.Add("⚠ 未配置场景名称（连接设置），等待配置", "#fbbf24"); return; }
        if (mapStations.Count == 0) { _logs.Add("⚠ 地图站点未上传（地图信息页上传或直接调用 /api/wcs/map），等待地图", "#fbbf24"); return; }
        var range = _rangeConfig.Get();

        // 1. 查库存
        var (ok, httpCode, json) = await _grcs.QueryCargoInventoryAsync(settings.GrcsBaseUrl, settings.SceneName);
        if (!ok || string.IsNullOrEmpty(json))
        { _logs.Add("⚠ 库存查询失败（HTTP " + httpCode + "），等待下轮", "#fbbf24"); return; }
        CargoQueryResult? result = null;
        try { result = JsonSerializer.Deserialize<CargoQueryResult>(json, Opts); } catch { }
        var records = result?.Data?.Records ?? [];

        // 3. 站点池（先建池并应用选点范围，再用范围储位集做库存判定，保证范围外托盘不被下发）
        var storages = mapStations.Where(s => s.StaEnable && (s.StationType & MapStationTypeBits.StorageLocation) != 0).ToList();
        var transferPoints = mapStations.Where(s => s.StaEnable && (s.StationType & MapStationTypeBits.TransferPoint) != 0).ToList();
        var pickingStations = mapStations.Where(s => s.StaEnable && (s.StationType & MapStationTypeBits.PeopleStation) != 0).ToList();
        ApplyRange(range, storages, transferPoints, pickingStations);

        // 2. 配对分析：只认（范围内）储位内的空托/带货托（接驳位、分拣台等非储位站点不参与，
        //    否则段1 搬到接驳位等装货的托盘会被误判为空托而重复下发入库）
        var storageMarks = storages.Select(s => s.Mark).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pallets = new List<(string Code, string Station)>();
        var cargos = new List<(string Code, string Station)>();
        int lockedCnt = 0, loadedCnt = 0;
        foreach (var c in records)
        {
            if (c.IsLocked) { lockedCnt++; continue; }
            if (c.IsLoaded) { loadedCnt++; continue; }
            if (string.IsNullOrEmpty(c.Code) || string.IsNullOrEmpty(c.CurrentStationCode)) continue;
            var stationCode = c.CurrentStationCode; // 库存站点码带 _0/_1 后缀，归一化到地图 Mark 再匹配
            if (stationCode.Length > 2 && (stationCode[^2..] is "_0" or "_1")) stationCode = stationCode[..^2];
            if (!storageMarks.Contains(stationCode)) continue;
            if (c.IsPallet()) pallets.Add((c.Code, stationCode));
            else if (c.IsCargo()) cargos.Add((c.Code, stationCode));
        }
        var cargoMarks = new HashSet<string>(cargos.Select(c => c.Station), StringComparer.OrdinalIgnoreCase);
        var palletMarks = new HashSet<string>(pallets.Select(p => p.Station), StringComparer.OrdinalIgnoreCase);
        var emptyPallets = pallets.Where(p => !cargoMarks.Contains(p.Station)).ToList();
        var loadedPallets = pallets.Where(p => cargoMarks.Contains(p.Station)).ToList();
        var pairedCargos = cargos.Where(c => palletMarks.Contains(c.Station)).ToList();

        // 快照给 /auto/status
        Inventory.EmptyPallets = emptyPallets.Count;
        Inventory.LoadedPallets = loadedPallets.Count;
        Inventory.Cargos = cargos.Count(c => !palletMarks.Contains(c.Station));
        Inventory.PairedCargos = pairedCargos.Count;

        // 3b. 终点储位锁定过滤
        var lockedStations = _locks.GetLocked(_stages);
        var occupiedMarks = new HashSet<string>(pallets.Select(p => p.Station).Concat(cargos.Select(c => c.Station)), StringComparer.OrdinalIgnoreCase);
        var emptyStorages = storages.Where(s => !occupiedMarks.Contains(s.Mark) && !lockedStations.Contains(s.Mark)).ToList();

        // 4. 下发：空托 → 入库；带货托 → 按模式（托盘发现即占用，流程结束才解除）
        var found = 0; var busy = 0;
        foreach (var (code, loc) in emptyPallets)
        {
            if (_handled.Contains(code)) { busy++; continue; }
            _handled.Add(code);
            found++;
            _ = ProcessInboundAsync(code, loc, transferPoints, emptyStorages);
        }

        // FlowMode 3「无任务」：空托照常入库；带货托不再触发出库/分拣，只留在储位
        if (FlowMode == 3)
        {
            var skipped = loadedPallets.Count;
            string msg3;
            if (found > 0) msg3 = "轮询完成：发现 " + found + " 个空托开始入库（带货托 " + skipped + " 个按「无任务」跳过）";
            else if (busy > 0) msg3 = "轮询完成：无新空托可入库（" + busy + " 个已在处理中；带货托 " + skipped + " 个按「无任务」跳过）";
            else if (skipped > 0) msg3 = "轮询完成：储位内无空托可入库（带货托 " + skipped + " 个按「无任务」跳过）";
            else msg3 = "轮询完成：储位内未发现可下发的空托" + (lockedCnt > 0 ? "（跳过锁定 " + lockedCnt + "）" : "");
            _logs.Add(msg3, found == 0 ? "#94a3b8" : "#4ade80");
            return;
        }

        foreach (var (code, loc) in loadedPallets)
        {
            if (_handled.Contains(code)) { busy++; continue; }
            var cargo = pairedCargos.FirstOrDefault(c => c.Station == loc);
            if (cargo.Code == null) { _logs.Add("⚠ 带货托盘 " + code + "@" + loc + " 无配对货物，无法下发", "#fbbf24"); continue; }
            _handled.Add(code);
            found++;
            if (FlowMode == 1) _ = ProcessSortingAsync(code, cargo.Code, loc, pickingStations);
            else if (FlowMode == 2) _ = ProcessOutboundAsync(code, cargo.Code, loc, transferPoints, emptyStorages);
            else if (_outboundTurn++ % 2 == 0) _ = ProcessOutboundAsync(code, cargo.Code, loc, transferPoints, emptyStorages);
            else _ = ProcessSortingAsync(code, cargo.Code, loc, pickingStations);
        }

        var visible = emptyPallets.Count + loadedPallets.Count;
        string msg;
        if (found > 0) msg = "轮询完成：发现 " + found + " 个托盘开始处理（库存可见 " + visible + " 个，其中 " + busy + " 个已在处理中）";
        else if (busy > 0) msg = "轮询完成：无新托盘可下发（库存可见 " + visible + " 个，全部已在处理中）";
        else msg = "轮询完成：储位内未发现可下发的空托/带货托" + (lockedCnt + loadedCnt > 0 ? "（跳过在途 " + loadedCnt + " / 锁定 " + lockedCnt + "）" : "");
        _logs.Add(msg, found == 0 ? "#94a3b8" : "#4ade80");
    }

    private void ApplyRange(RangeConfigDto range, List<MapStationLite> storages, List<MapStationLite> transferPoints, List<MapStationLite> pickingStations)
    {
        if (!range.Enabled) return;
        var pool = range.ApplyTo(_mapStore.GetStations());
        storages.RemoveAll(s => !pool.Contains(s));
        transferPoints.RemoveAll(s => !pool.Contains(s));
        pickingStations.RemoveAll(s => !pool.Contains(s));
    }

    // ── 空托盘入库（段1 空托 → 段2 带载，段1 完成后货物码随卡片生成）──
    private async Task ProcessInboundAsync(string palletCode, string loc, List<MapStationLite> transferPoints, List<MapStationLite> emptyStorages)
    {
        try
        {
            if (transferPoints.Count == 0) { _logs.Add("❌ 托盘 " + palletCode + "@" + loc + " 无法下发入库：无可用接驳位", "#f87171"); Finish(palletCode); return; }
            if (emptyStorages.Count == 0) { _logs.Add("❌ 托盘 " + palletCode + "@" + loc + " 无法下发入库：无空储位（被锁定或已占用）", "#f87171"); Finish(palletCode); return; }
            var settings = _settings.Get();
            var rand = Random.Shared;
            var tpSt = transferPoints[rand.Next(transferPoints.Count)];
            var dstSt = emptyStorages[rand.Next(emptyStorages.Count)];
            emptyStorages.Remove(dstSt);
            var srcSt = _mapStore.GetStations().FirstOrDefault(s => s.Mark == loc);
            var seq = ++_seq;
            var id1 = "SimAuto_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x").ToUpper() + "_A" + seq + "a";
            var id2 = id1[..^1] + "b";
            _logs.Add("发现空托盘 " + palletCode + "@" + loc + " → 下发入库段1 " + id1 + "（储位 " + (srcSt?.Mark ?? loc) + " → 接驳位 " + tpSt.Mark + "），段2 回储位 " + dstSt.Mark, "#60a5fa");
            _locks.Acquire(dstSt.Mark, id2);

            var payload1 = new WcsTaskGroup
            {
                GroupId = "G_" + id1, MsgTime = DateTime.Now.ToString("O"), PriorityCode = 50, Warehouse = settings.SceneName,
                Tasks = [new WcsTaskItem { TaskId = id1, TaskType = "CONTAINER_CARRY_INBOUND", ContainerCode = palletCode, StationCode = [srcSt?.ToWcsCode() ?? loc, tpSt.ToWcsCode()] }]
            };
            var (ok1, code1, _) = await _grcs.SendTaskGroupAsync(settings.GrcsBaseUrl, payload1);
            await _ledger.AppendAsync([new TaskLedgerEntry { TaskId = id1, TaskType = "CONTAINER_CARRY_INBOUND", ContainerCode = palletCode, CargoCode = "", StationCode = payload1.Tasks[0].StationCode, Warehouse = settings.SceneName, Time = DateTime.Now.ToString("O"), Ok = ok1, StatusCode = code1 }]);
            if (!ok1) { _logs.Add("❌ 入库段1 下发失败 HTTP" + code1 + "：" + palletCode, "#f87171"); Finish(palletCode); return; }
            Dispatched++;

            try { await _stages.WaitFinishedAsync(id1); }
            catch (TimeoutException) { _logs.Add("⚠ 段1 " + id1 + " 等待超时（10 分钟），托盘 " + palletCode + " 状态未知", "#fbbf24"); Finish(palletCode); return; }
            _logs.Add("段1 " + id1 + " 完成，托盘 " + palletCode + " 已到接驳位", "#4ade80");

            var cargoCode = _cargoCodes.Ensure(id1);
            var payload2 = new WcsTaskGroup
            {
                GroupId = "G_" + id2, MsgTime = DateTime.Now.ToString("O"), PriorityCode = 50, Warehouse = settings.SceneName,
                Tasks = [new WcsTaskItem { TaskId = id2, TaskType = "CARGO_CARRY_INBOUND", ContainerCode = cargoCode, StationCode = [tpSt.ToWcsCode(), dstSt.ToWcsCode()] }]
            };
            var (ok2, code2, _) = await _grcs.SendTaskGroupAsync(settings.GrcsBaseUrl, payload2);
            await _ledger.AppendAsync([new TaskLedgerEntry { TaskId = id2, TaskType = "CARGO_CARRY_INBOUND", ContainerCode = palletCode, CargoCode = cargoCode, StationCode = payload2.Tasks[0].StationCode, Warehouse = settings.SceneName, Time = DateTime.Now.ToString("O"), Ok = ok2, StatusCode = code2 }]);
            if (ok2) { Dispatched++; _logs.Add("✓ 入库段2 " + id2 + " 已下发（货物码 " + cargoCode + "，托盘 " + palletCode + "）", "#4ade80"); }
            else _logs.Add("❌ 入库段2 下发失败 HTTP" + code2 + "（托盘 " + palletCode + "）", "#f87171");
        }
        catch (Exception ex) { _logs.Add("❌ 入库流程异常（托盘 " + palletCode + "）: " + ex.Message, "#f87171"); }
        Finish(palletCode);
    }

    // ── 带货托盘出库（段1 带载 → 段2 空托回库）──
    private async Task ProcessOutboundAsync(string palletCode, string cargoCode, string loc, List<MapStationLite> transferPoints, List<MapStationLite> emptyStorages)
    {
        try
        {
            if (transferPoints.Count == 0) { _logs.Add("❌ 带货托盘 " + palletCode + "@" + loc + " 无法下发出库：无可用接驳位", "#f87171"); Finish(palletCode); return; }
            if (emptyStorages.Count == 0) { _logs.Add("❌ 带货托盘 " + palletCode + "@" + loc + " 无法下发出库：无空储位（被锁定或已占用）", "#f87171"); Finish(palletCode); return; }
            var settings = _settings.Get();
            var rand = Random.Shared;
            var tpSt = transferPoints[rand.Next(transferPoints.Count)];
            var dstSt = emptyStorages[rand.Next(emptyStorages.Count)];
            emptyStorages.Remove(dstSt);
            var srcSt = _mapStore.GetStations().FirstOrDefault(s => s.Mark == loc);
            var seq = ++_seq;
            var id1 = "SimAuto_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x").ToUpper() + "_A" + seq + "a";
            var id2 = id1[..^1] + "b";
            _logs.Add("发现带货托盘 " + palletCode + "（货 " + cargoCode + "）@" + loc + " → 下发出库段1 " + id1 + "（储位 " + (srcSt?.Mark ?? loc) + " → 接驳位 " + tpSt.Mark + "），段2 回库储位 " + dstSt.Mark, "#60a5fa");
            _locks.Acquire(dstSt.Mark, id2);

            var payload1 = new WcsTaskGroup
            {
                GroupId = "G_" + id1, MsgTime = DateTime.Now.ToString("O"), PriorityCode = 50, Warehouse = settings.SceneName,
                Tasks = [new WcsTaskItem { TaskId = id1, TaskType = "CARGO_CARRY_OUTBOUND", ContainerCode = cargoCode, StationCode = [srcSt?.ToWcsCode() ?? loc, tpSt.ToWcsCode()] }]
            };
            var (ok1, code1, _) = await _grcs.SendTaskGroupAsync(settings.GrcsBaseUrl, payload1);
            await _ledger.AppendAsync([new TaskLedgerEntry { TaskId = id1, TaskType = "CARGO_CARRY_OUTBOUND", ContainerCode = palletCode, CargoCode = cargoCode, StationCode = payload1.Tasks[0].StationCode, Warehouse = settings.SceneName, Time = DateTime.Now.ToString("O"), Ok = ok1, StatusCode = code1 }]);
            if (!ok1) { _logs.Add("❌ 出库段1 下发失败 HTTP" + code1 + "：" + palletCode, "#f87171"); Finish(palletCode); return; }
            Dispatched++;

            try { await _stages.WaitFinishedAsync(id1); }
            catch (TimeoutException) { _logs.Add("⚠ 段1 " + id1 + " 等待超时（10 分钟），货物 " + cargoCode + " 状态未知", "#fbbf24"); Finish(palletCode); return; }
            _logs.Add("段1 " + id1 + " 完成，货物 " + cargoCode + " 已到接驳位", "#4ade80");

            var payload2 = new WcsTaskGroup
            {
                GroupId = "G_" + id2, MsgTime = DateTime.Now.ToString("O"), PriorityCode = 50, Warehouse = settings.SceneName,
                Tasks = [new WcsTaskItem { TaskId = id2, TaskType = "CONTAINER_CARRY_OUTBOUND", ContainerCode = palletCode, StationCode = [tpSt.ToWcsCode(), dstSt.ToWcsCode()] }]
            };
            var (ok2, code2, _) = await _grcs.SendTaskGroupAsync(settings.GrcsBaseUrl, payload2);
            await _ledger.AppendAsync([new TaskLedgerEntry { TaskId = id2, TaskType = "CONTAINER_CARRY_OUTBOUND", ContainerCode = palletCode, CargoCode = cargoCode, StationCode = payload2.Tasks[0].StationCode, Warehouse = settings.SceneName, Time = DateTime.Now.ToString("O"), Ok = ok2, StatusCode = code2 }]);
            if (ok2) { Dispatched++; _logs.Add("✓ 出库段2 " + id2 + " 已下发（空托 " + palletCode + " 回库）", "#4ade80"); }
            else _logs.Add("❌ 出库段2 下发失败 HTTP" + code2 + "（托盘 " + palletCode + "）", "#f87171");
        }
        catch (Exception ex) { _logs.Add("❌ 出库流程异常（托盘 " + palletCode + "）: " + ex.Message, "#f87171"); }
        Finish(palletCode);
    }

    // ── 带货托盘分拣（只有段1，完成即流程结束）──
    private async Task ProcessSortingAsync(string palletCode, string cargoCode, string loc, List<MapStationLite> pickingStations)
    {
        try
        {
            if (pickingStations.Count == 0) { _logs.Add("❌ 带货托盘 " + palletCode + "@" + loc + " 无法下发分拣：无人工分拣台", "#f87171"); Finish(palletCode); return; }
            var settings = _settings.Get();
            var rand = Random.Shared;
            var pickSt = pickingStations[rand.Next(pickingStations.Count)];
            var srcSt = _mapStore.GetStations().FirstOrDefault(s => s.Mark == loc);
            var seq = ++_seq;
            var id1 = "SimAuto_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x").ToUpper() + "_A" + seq + "a";
            _logs.Add("发现带货托盘 " + palletCode + "（货 " + cargoCode + "）@" + loc + " → 下发分拣 " + id1 + "（" + (srcSt?.Mark ?? loc) + " → 分拣台 " + pickSt.Mark + "）", "#60a5fa");

            var payload1 = new WcsTaskGroup
            {
                GroupId = "G_" + id1, MsgTime = DateTime.Now.ToString("O"), PriorityCode = 50, Warehouse = settings.SceneName,
                Tasks = [new WcsTaskItem { TaskId = id1, TaskType = "SORTING", ContainerCode = cargoCode, StationCode = [srcSt?.ToWcsCode() ?? loc, pickSt.ToWcsCode()] }]
            };
            var (ok1, code1, _) = await _grcs.SendTaskGroupAsync(settings.GrcsBaseUrl, payload1);
            await _ledger.AppendAsync([new TaskLedgerEntry { TaskId = id1, TaskType = "SORTING", ContainerCode = palletCode, CargoCode = cargoCode, StationCode = payload1.Tasks[0].StationCode, Warehouse = settings.SceneName, Time = DateTime.Now.ToString("O"), Ok = ok1, StatusCode = code1 }]);
            if (!ok1) { _logs.Add("❌ 分拣下发失败 HTTP" + code1 + "：" + palletCode, "#f87171"); Finish(palletCode); return; }
            Dispatched++;

            try { await _stages.WaitFinishedAsync(id1); }
            catch (TimeoutException) { _logs.Add("⚠ 分拣 " + id1 + " 等待超时（10 分钟），货物 " + cargoCode + " 状态未知", "#fbbf24"); Finish(palletCode); return; }
            _logs.Add("✓ 分拣 " + id1 + " 完成，货物 " + cargoCode + " 已到分拣台", "#4ade80");
        }
        catch (Exception ex) { _logs.Add("❌ 分拣流程异常（托盘 " + palletCode + "）: " + ex.Message, "#f87171"); }
        Finish(palletCode);
    }

    private void Finish(string palletCode) => _handled.Remove(palletCode);

    public void Dispose() { _cts?.Cancel(); _cts?.Dispose(); }
}
