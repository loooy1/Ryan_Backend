using GrcsBackend.Modules.Wcs.Proxy.Services;
using GrcsBackend.Modules.Wcs.Infrastructure.Models;
using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Modules.Wcs.Automation.Services.TWD;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Console.Controllers.TWD;

/// <summary>
/// 自动化控制台接口（/api/wcs/auto/*，供前端遥控；不是 GRCS 协议接口）。
/// 启停/参数/状态/日志（sinceId 增量）/选点范围/批量执行/信号自动开关。
/// </summary>
[ApiController]
[Route("api/wcs/auto")]
public class AutomationConsoleController : ControllerBase
{
    private readonly AutoRunHostedService _auto;
    private readonly ContainerTaskRunner _runner;
    private readonly AutomationLogService _logs;
    private readonly RangeConfigService _rangeConfig;
    private readonly WcsSettingsService _settings;
    private readonly SignalAutoHostedService _signals;
    private readonly AutomationGate _gate;
    private readonly GrcsHttpClient _grcs;

    public AutomationConsoleController(AutoRunHostedService auto, ContainerTaskRunner runner, AutomationLogService logs,
        RangeConfigService rangeConfig, WcsSettingsService settings, SignalAutoHostedService signals, AutomationGate gate, GrcsHttpClient grcs)
    {
        _auto = auto;
        _runner = runner;
        _logs = logs;
        _rangeConfig = rangeConfig;
        _settings = settings;
        _signals = signals;
        _gate = gate;
        _grcs = grcs;
    }

    /// <summary>整体状态快照（前端 2s 轮询）。dispatchActive=任一下发模式进行中（前端跨标签页警示/禁用判断）。</summary>
    [HttpGet("status")]
    public ActionResult<object> Status()
    {
        return Ok(new
        {
            running = _auto.Running,
            autoTabId = _gate.AutoTabId,
            interval = _auto.Interval,
            flowMode = _auto.FlowMode,
            dispatched = _auto.Dispatched,
            status = _auto.Status,
            autoInventory = _auto.Inventory,
            containerBusy = _runner.Busy,
            batchTabId = _gate.BatchTabId,
            containerDone = _runner.Done,
            containerTotal = _runner.Total,
            containerStatus = _runner.Status,
            containerInventory = _runner.Inventory,
            moveRunning = _gate.MoveRunning,
            moveTabId = _gate.MoveTabId,
            dispatchActive = _gate.AnyRunning,
            settings = _settings.Get(),
            signals = new { admittanceAuto = _signals.AdmittanceAuto, arrivalAuto = _signals.ArrivalAuto, removalAuto = _signals.RemovalAuto, autoSend = _signals.AutoSend },
        });
    }

    [HttpPost("start")]
    public ActionResult<object> Start([FromBody] TabRequest? req)
    {
        var ok = _auto.Start(req?.TabId);
        return Ok(new { success = ok, running = _auto.Running });
    }

    [HttpPost("stop")]
    public ActionResult<object> Stop([FromBody] TabRequest? req)
    {
        _auto.Stop();
        return Ok(new { running = false });
    }

    [HttpPost("interval")]
    public ActionResult<object> SetInterval([FromBody] IntervalRequest req)
    {
        _auto.Interval = Math.Clamp(req.Interval, 1, 60);
        return Ok(new { interval = _auto.Interval });
    }

    [HttpPost("flowmode")]
    public ActionResult<object> SetFlowMode([FromBody] FlowModeRequest req)
    {
        _auto.FlowMode = Math.Clamp(req.FlowMode, 0, 3);
        return Ok(new { flowMode = _auto.FlowMode });
    }

    /// <summary>增量日志（sinceId &gt; 0 只返回新条目；不带返回最近 500 条，Id 最大值为水位）。</summary>
    [HttpGet("logs")]
    public ActionResult<object> Logs([FromQuery] long? sinceId)
    {
        var entries = sinceId is > 0 ? _logs.GetSince(sinceId.Value) : _logs.GetSince(0);
        return Ok(new { maxId = _logs.MaxId, entries });
    }

    [HttpDelete("logs")]
    public ActionResult<object> ClearLogs() { _logs.Clear(); return Ok(new { success = true }); }

    [HttpGet("range")]
    public ActionResult<RangeConfigDto> Range() => Ok(_rangeConfig.Get());

    [HttpPut("range")]
    public ActionResult<object> SaveRange([FromBody] RangeConfigDto range)
    {
        _rangeConfig.Set(range);
        return Ok(new { success = true });
    }

    [HttpGet("settings")]
    public ActionResult<WcsSettingsDto> Settings() => Ok(_settings.Get());

    [HttpPut("settings")]
    public ActionResult<object> SaveSettings([FromBody] WcsSettingsDto settings)
    {
        _settings.Set(settings);
        return Ok(new { success = true });
    }

    /// <summary>批量容器任务执行（body: flow/count/interval/tabId）。</summary>
    [HttpPost("container/execute")]
    public async Task<ActionResult<string>> ContainerExecute([FromBody] ContainerExecuteRequest req)
    {
        var result = await _runner.ExecuteAsync(req.Flow, req.Count, req.Interval, req.TabId);
        return Ok(new { message = result });
    }

    /// <summary>批量容器任务查询库存。</summary>
    [HttpPost("container/refresh")]
    public async Task<ActionResult<string>> ContainerRefresh()
    {
        var result = await _runner.RefreshInventoryAsync();
        return Ok(new { message = result });
    }

    /// <summary>移动任务循环登记租约：未启用（无任何下发进行中）则取用并置为启用，否则拒绝其他标签页。</summary>
    [HttpPost("move/start")]
    public ActionResult<object> MoveStart([FromBody] TabRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TabId)) return Ok(new { success = false, reason = "缺少 tabId" });
        var (ok, reason) = _gate.TryStartMove(req.TabId);
        if (!ok) _logs.Add("❌ 移动任务循环被拒绝：" + reason, "#f87171");
        return Ok(new { success = ok, reason });
    }

    /// <summary>移动任务循环属主心跳续约（防属主标签页关闭后租约永久占用）。</summary>
    [HttpPost("move/beat")]
    public ActionResult<object> MoveBeat([FromBody] TabRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TabId)) return Ok(new { success = false });
        return Ok(new { success = _gate.BeatMove(req.TabId) });
    }

    /// <summary>移动任务循环释放租约（停止时调用）。</summary>
    [HttpPost("move/stop")]
    public ActionResult<object> MoveStop([FromBody] TabRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.TabId)) _gate.StopMove(req.TabId);
        return Ok(new { success = true });
    }

    /// <summary>单条纯移动任务下发：WCS 前端 → 本接口 → GRCS /api/RawOrder/ChangeFloor。
    /// GRCS 地址与场景名从设置取（地图信息页保存）。</summary>
    [HttpPost("move/dispatch")]
    public async Task<ActionResult<object>> MoveDispatch([FromBody] VehicleOrderRequest order)
    {
        if (string.IsNullOrWhiteSpace(order?.OrderId)) return Ok(new { success = false, code = 0, json = "缺少订单 OrderId" });
        order.SceneName = _settings.Get().SceneName;
        var (ok, code, json) = await _grcs.SendVehicleOrderAsync(_settings.Get().GrcsBaseUrl, order);
        return Ok(new { success = ok, code, json });
    }

    /// <summary>信号自动开关（进入申请/到达/移除/分拣四档，字段可缺省：只改传了的档）。</summary>
    [HttpPost("signals")]
    public ActionResult<object> SetSignals([FromBody] SignalFlagsRequest req)
    {
        if (req.AdmittanceAuto.HasValue) _signals.SetAdmittance(req.AdmittanceAuto.Value);
        if (req.ArrivalAuto.HasValue) _signals.SetArrival(req.ArrivalAuto.Value);
        if (req.RemovalAuto.HasValue) _signals.SetRemoval(req.RemovalAuto.Value);
        if (req.AutoSend.HasValue) _signals.SetSorting(req.AutoSend.Value);
        return Ok(new { success = true });
    }
}

public class IntervalRequest { public int Interval { get; set; } }
public class FlowModeRequest { public int FlowMode { get; set; } }
public class TabRequest { public string? TabId { get; set; } }
public class ContainerExecuteRequest { public int Flow { get; set; } public int Count { get; set; } public int Interval { get; set; } public string? TabId { get; set; } }
public class SignalFlagsRequest
{
    public bool? AdmittanceAuto { get; set; }
    public bool? ArrivalAuto { get; set; }
    public bool? RemovalAuto { get; set; }
    public bool? AutoSend { get; set; }
}
