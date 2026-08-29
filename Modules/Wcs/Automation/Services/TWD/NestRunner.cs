using System.Text.Json;
using GrcsBackend.Modules.Wcs.Console.Services;
using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Modules.Wcs.Infrastructure.Models;
using GrcsBackend.Modules.Wcs.Proxy.Services;
using GrcsBackend.Modules.Wcs.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace GrcsBackend.Modules.Wcs.Automation.Services.TWD;

/// <summary>
/// 归巢模式：一次性批量下发（按钮触发）。
/// 流程：查 GRCS 全部车辆 → 过滤就绪车（在线 + IDLE + AUTOMATIC + 无当前任务）→
/// 以配置的巢点为中心，取巢点同层优先的普通道路点（不够补其他楼层），
/// 按距离取 N 个（N = 就绪车数）→ 每台车一条 MOVE_ONLY（VehicleName 指定车）串行下发。
/// 统计/日志/状态经 SignalR（/hubs/task-stages）广播 NestStats 回推前端；运行中防重入。
/// </summary>
public class NestRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly GrcsHttpClient _grcs;
    private readonly MapStoreService _map;
    private readonly NestConfigService _nestConfig;
    private readonly WcsSettingsService _settings;
    private readonly AutomationLogService _logs;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;

    private readonly object _stateLock = new();
    private bool _running;
    private string? _lastRunAt;
    private List<string> _readyVehicles = [];
    private int _ok;
    private int _fail;
    private string? _lastError;

    public NestRunner(GrcsHttpClient grcs, MapStoreService map, NestConfigService nestConfig,
        WcsSettingsService settings, AutomationLogService logs, IHubContext<TaskStageRealtimeHub> hub)
    {
        _grcs = grcs;
        _map = map;
        _nestConfig = nestConfig;
        _settings = settings;
        _logs = logs;
        _hub = hub;
    }

    // ── 状态（前端卡片 / status 接口共用）──
    public bool Running { get { lock (_stateLock) { return _running; } } }
    public string? LastRunAt { get { lock (_stateLock) { return _lastRunAt; } } }

    public NestStatsDto Snapshot()
    {
        lock (_stateLock)
        {
            return new NestStatsDto
            {
                Running = _running,
                LastRunAt = _lastRunAt,
                ReadyVehicles = _readyVehicles.ToList(),
                Ok = _ok,
                Fail = _fail,
                LastError = _lastError,
            };
        }
    }

    /// <summary>执行一次归巢（异步后台）。运行中再次调用直接忽略。</summary>
    public (bool Ok, string? Reason) Run()
    {
        lock (_stateLock)
        {
            if (_running) return (false, "归巢执行中，请稍候");
            _running = true;
            _ok = 0;
            _fail = 0;
            _lastError = null;
            _readyVehicles = [];
            _lastRunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        _ = RunCoreAsync();
        Broadcast();
        return (true, null);
    }

    private async Task RunCoreAsync()
    {
        try
        {
            var settings = _settings.Get();
            if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl))
            {
                await FailAsync("未配置 GRCS 地址（地图信息页系统设置）");
                return;
            }

            // 1. 查全部车辆
            var (vOk, vCode, vJson) = await _grcs.QueryVehiclesAsync(settings.GrcsBaseUrl, settings.SceneName);
            if (!vOk)
            {
                var reason = vCode == 0 ? "⚠ 查询车辆超时/网络异常" : $"查询车辆 HTTP {vCode}";
                _logs.Add("❌ 归巢模式：" + reason + $"\nGRCS 响应: {vJson}", "#f87171");
                await FailAsync(reason);
                return;
            }
            List<VehicleInfoDto> vehicles;
            try { vehicles = JsonSerializer.Deserialize<List<VehicleInfoDto>>(vJson, JsonOpts) ?? []; }
            catch { vehicles = []; }

            // 2. 过滤就绪车（在线 + IDLE + AUTOMATIC + 无当前任务）
            var ready = vehicles.Where(v => v.IsReady).ToList();
            lock (_stateLock) { _readyVehicles = ready.Select(v => v.Name).ToList(); }

            if (ready.Count == 0)
            {
                _logs.Add($"🧹 归巢模式：查询到 {vehicles.Count} 台车，0 台就绪（需 在线+空闲+自动+无任务），未下发", "#fbbf24");
                await FinishAsync();
                return;
            }

            // 3. 巢点
            var nestMark = _nestConfig.Get().NestMark;
            var stations = _map.GetStations();
            var nest = stations.FirstOrDefault(s => string.Equals(s.Mark, nestMark, StringComparison.OrdinalIgnoreCase));
            if (nest == null)
            {
                var reason = $"巢点站点「{nestMark}」不存在（站点池 {stations.Count} 个，请检查巢点设置）";
                _logs.Add("❌ 归巢模式：" + reason, "#f87171");
                await FailAsync(reason);
                return;
            }

            // 4. 选点：普通道路点，与巢点至少间隔一个点（防拥挤），巢点同层优先按欧氏距离升序，不够补其他楼层，取 N 个
            var roads = stations.Where(s => s.StaEnable && (s.StationType & MapStationTypeBits.NormalRoad) != 0).ToList();
            var minSpacing = EstimateGridSpacing(roads);
            var minDistToNest = 2 * minSpacing;
            var candidates = roads
                .Where(s => Math.Sqrt(Math.Pow(s.X - nest.X, 2) + Math.Pow(s.Y - nest.Y, 2)) >= minDistToNest)
                .Select(s => new { s, d = Math.Sqrt(Math.Pow(s.X - nest.X, 2) + Math.Pow(s.Y - nest.Y, 2)) })
                .OrderBy(x => x.s.Floor == nest.Floor ? 0 : 1)
                .ThenBy(x => x.d)
                .Select(x => x.s)
                .ToList();
            var targets = candidates.Take(ready.Count).ToList();

            _logs.Add($"▶ 归巢模式开始：巢点 {nest.Mark}（{nest.Floor} 层），就绪车 {ready.Count} 台，"
                + $"普通路点 {roads.Count} 个，候选（距巢点 ≥ {minDistToNest:0.#}，至少间隔一个点）{candidates.Count} 个，本次分配 {targets.Count} 个", "#60a5fa");

            // 5. 逐台车串行下发
            for (var i = 0; i < ready.Count; i++)
            {
                var vehicle = ready[i];
                var target = i < targets.Count ? targets[i] : null;
                if (target == null)
                {
                    _logs.AddOrUpdate("[归巢·未分配]", $"车辆 {vehicle.Name}：普通路点不足，未分配目标点", "#fbbf24");
                    continue;
                }
                await DispatchAsync(vehicle, target, settings);
                if (Running) Broadcast();
            }

            await FinishAsync();
        }
        catch (Exception ex)
        {
            _logs.Add("❌ 归巢模式异常：" + ex.Message, "#f87171");
            await FailAsync(ex.Message);
        }
    }

    /// <summary>估算普通路点网格最小间距（任意两点的最小距离；不足两点时取 1，防除零）。</summary>
    private static double EstimateGridSpacing(List<MapStationLite> roads)
    {
        if (roads.Count < 2) return 1;
        var spacing = double.MaxValue;
        for (var i = 0; i < roads.Count; i++)
        {
            for (var j = i + 1; j < roads.Count; j++)
            {
                var d = Math.Sqrt(Math.Pow(roads[i].X - roads[j].X, 2) + Math.Pow(roads[i].Y - roads[j].Y, 2));
                if (d > 0.001 && d < spacing) spacing = d;
            }
        }
        return spacing == double.MaxValue ? 1 : spacing;
    }

    private async Task DispatchAsync(VehicleInfoDto vehicle, MapStationLite target, WcsSettingsDto settings)
    {
        var nest = _map.GetStations().FirstOrDefault(s => string.Equals(s.Mark, _nestConfig.Get().NestMark, StringComparison.OrdinalIgnoreCase));
        var dist = nest == null ? 0 : Math.Sqrt(Math.Pow(target.X - nest.X, 2) + Math.Pow(target.Y - nest.Y, 2));
        var payload = new VehicleOrderRequest
        {
            CreateTime = DateTime.Now,
            SceneName = settings.SceneName,
            OrderType = "MOVE_ONLY",
            OrderId = $"NestHome_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x").ToUpper()}_{vehicle.Name.GetHashCode():x}",
            OrderName = "wcs模拟器归巢任务",
            VehicleName = vehicle.Name,
            Priority = 50,
            StationCodes = [target.Mark],
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, code, json) = await _grcs.SendVehicleOrderAsync(settings.GrcsBaseUrl, payload);
        sw.Stop();
        var elapsedMs = sw.ElapsedMilliseconds;
        var bodyJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        lock (_stateLock)
        {
            if (ok)
            {
                _ok++;
                _lastError = null;
                _logs.AddOrUpdate("[归巢·成功]", $"车辆 {vehicle.Name} → {target.Mark}（{target.Floor} 层，距巢点 {dist:0.#}m）✓ 任务号：（{payload.OrderId}）（耗时 {elapsedMs}ms）\n请求体: {bodyJson}", "#4ade80");
            }
            else
            {
                _fail++;
                var reason = code == 0 ? "⚠ 超时/网络异常" : $"HTTP {code}";
                _lastError = reason;
                _logs.AddOrUpdate("[归巢·失败]", $"车辆 {vehicle.Name} → {target.Mark}：{reason}（{payload.OrderId}）（耗时 {elapsedMs}ms）\n请求体: {bodyJson}\nGRCS 响应: {json}", "#f87171");
            }
        }
    }

    private void Finish() => _logs.Add($"✅ 归巢模式执行完成：共 {_readyVehicles.Count} 台车，成功 {_ok} / 失败 {_fail}", "#4ade80");

    private async Task FinishAsync()
    {
        Finish();
        lock (_stateLock) { _running = false; }
        Broadcast();
    }

    private async Task FailAsync(string reason)
    {
        lock (_stateLock) { _lastError = reason; _running = false; }
        Broadcast();
    }

    /// <summary>SignalR 广播归巢状态。</summary>
    private void Broadcast() => _hub.Clients.All.SendAsync("NestStats", Snapshot());
}