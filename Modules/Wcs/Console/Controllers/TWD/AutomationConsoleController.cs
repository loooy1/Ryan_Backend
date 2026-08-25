using GrcsBackend.Modules.Wcs.Proxy.Services;
using GrcsBackend.Modules.Wcs.Infrastructure.Models;
using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Modules.Wcs.Automation.Services.TWD;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace GrcsBackend.Modules.Wcs.Console.Controllers.TWD;

/// <summary>
/// 自动化控制台接口（/api/wcs/auto/*，供前端遥控；不是 GRCS 协议接口）。
/// 启停/参数/状态/日志（sinceId 增量）/选点范围/自动化模板 CRUD/单次执行/移动任务循环/信号自动开关。
/// 旧的两段式轮询（AutoRunHostedService）与批量容器（ContainerTaskRunner）已移除，统一由
/// AutoTemplateRunner + 用户自定义自动化模板取代。
/// </summary>
[ApiController]
[Route("api/wcs/auto")]
public class AutomationConsoleController : ControllerBase
{
    private readonly AutoTemplateRunner _auto;
    private readonly AutoTemplateStore _templates;
    private readonly AutomationLogService _logs;
    private readonly RangeConfigService _rangeConfig;
    private readonly WcsSettingsService _settings;
    private readonly SignalAutoHostedService _signals;
    private readonly AutomationGate _gate;
    private readonly GrcsHttpClient _grcs;
    /// <summary>本轮纯移动任务下发成功数 / 失败数（系统日志实时计数用，开始/停止时归零）。</summary>
    private int _moveOkCount;
    private int _moveFailCount;

    public AutomationConsoleController(AutoTemplateRunner auto, AutoTemplateStore templates, AutomationLogService logs,
        RangeConfigService rangeConfig, WcsSettingsService settings, SignalAutoHostedService signals, AutomationGate gate, GrcsHttpClient grcs)
    {
        _auto = auto;
        _templates = templates;
        _logs = logs;
        _rangeConfig = rangeConfig;
        _settings = settings;
        _signals = signals;
        _gate = gate;
        _grcs = grcs;
    }

    /// <summary>库存分类汇总（纯空托 / 带货托 / 纯货物 / 锁定中），按「以前逻辑」在后端统计。</summary>
    [HttpGet("inventory-summary")]
    public async Task<ActionResult<object>> InventorySummary()
    {
        var (empty, loaded, cargo, locked) = await _auto.GetInventorySummaryAsync();
        return Ok(new { empty, loaded, cargo, locked });
    }

    /// <summary>整体状态快照（前端 2s 轮询）。dispatchActive=任一下发模式进行中（前端跨标签页警示/禁用判断）。</summary>
    [HttpGet("status")]
    public ActionResult<object> Status()
    {
        var tpl = _auto.Snapshot();
        return Ok(new
        {
            running = tpl.Running,
            autoTabId = tpl.ActiveTabId,
            interval = tpl.Interval,
            activeTemplateId = tpl.ActiveTemplateId,
            activeTemplateName = tpl.ActiveTemplateName,
            executed = tpl.Executed,
            status = tpl.Status,
            dispatchActive = tpl.AnyRunning,
            moveRunning = _gate.MoveRunning,
            moveTabId = _gate.MoveTabId,
            templates = _templates.GetAll(),
            settings = _settings.Get(),
            signals = new { arrivalAuto = _signals.ArrivalAuto, removalAuto = _signals.RemovalAuto, autoSend = _signals.AutoSend },
        });
    }

    [HttpPost("start")]
    public ActionResult<object> Start([FromBody] StartRequest? req)
    {
        var (ok, reason) = _auto.Start(req?.TabId, req?.TemplateIds);
        return Ok(new { success = ok, message = reason ?? "", running = _auto.Running });
    }

    [HttpPost("stop")]
    public ActionResult<object> Stop()
    {
        _auto.Stop();
        return Ok(new { running = false });
    }

    [HttpPost("force-stop")]
    public ActionResult<object> ForceStop()
    {
        _auto.ForceEnd();
        return Ok(new { success = true });
    }

    [HttpPost("interval")]
    public ActionResult<object> SetInterval([FromBody] IntervalRequest req)
    {
        _auto.SetInterval(req.Interval * 1000);
        return Ok(new { interval = _auto.Interval });
    }

    /// <summary>单次执行：立即按模板执行 count 次（步间间隔 interval 秒）。</summary>
    [HttpPost("execute")]
    public async Task<ActionResult<object>> Execute([FromBody] ExecuteRequest req)
    {
        await _auto.ExecuteOnce(req.TemplateId, req.Count, (req.Interval) * 1000, req.TabId);
        return Ok(new { success = true });
    }

    // ── 自动化模板 CRUD（跨浏览器共享，存 auto_templates 表）──

    [HttpGet("templates")]
    public ActionResult<List<AutoTemplateDto>> Templates() => Ok(_templates.GetAll());

    /// <summary>整体替换（前端保存全部模板后整体回写）。保存前校验各步骤引用的任务模板其起点/终点类型
    /// 在选点范围内存在对应站点，校验失败返回 400 并附带错误列表（前端据此提示，不在运行时跳过）。</summary>
    [HttpPut("templates")]
    public ActionResult<object> SaveTemplates([FromBody] List<AutoTemplateDto> items)
    {
        var errors = _auto.ValidateTemplates(items ?? []);
        if (errors.Count > 0)
            return BadRequest(new { success = false, errors });
        _templates.ReplaceAll(items ?? []);
        return Ok(new { success = true, count = _templates.GetAll().Count });
    }

    [HttpDelete("templates")]
    public ActionResult<object> DeleteTemplate([FromQuery] string id)
    {
        var ok = _templates.Remove(id);
        return Ok(new { success = ok });
    }

    /// <summary>按轮次分组的日志（每轮一个标题，含该轮所有条目）。</summary>
    [HttpGet("logs")]
    public ActionResult<object> Logs() => Ok(_logs.GetRounds());

    [HttpDelete("logs")]
    public ActionResult<object> ClearLogs() { _logs.Clear(); return Ok(new { success = true }); }

    [HttpDelete("logs/system")]
    public ActionResult<object> ClearSystemLogs() { _logs.ClearSystem(); return Ok(new { success = true }); }

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

    /// <summary>移动任务循环登记租约：未启用（无任何下发进行中）则取用并置为启用，否则拒绝其他标签页。</summary>
    [HttpPost("move/start")]
    public ActionResult<object> MoveStart([FromBody] TabRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TabId)) return Ok(new { success = false, reason = "缺少 tabId" });
        var (ok, reason) = _gate.TryStartMove(req.TabId);
        if (!ok) _logs.Add("❌ 移动任务循环被拒绝：" + reason, "#f87171");
        else
        {
            _moveOkCount = 0;
            _moveFailCount = 0;
            _logs.Add("▶ 纯移动任务循环开始（自动下发 MOVE_ONLY，日志实时更新计数）", "#60a5fa");
        }
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
        var total = _moveOkCount + _moveFailCount;
        _logs.Add($"⏹ 纯移动任务循环已停止（共下发 {total} 个任务：成功 {_moveOkCount} / 失败 {_moveFailCount}）", "#fbbf24");
        _moveOkCount = 0;
        _moveFailCount = 0;
        if (!string.IsNullOrWhiteSpace(req.TabId)) _gate.StopMove(req.TabId);
        return Ok(new { success = true });
    }

    /// <summary>单条纯移动任务下发：WCS 前端 → 本接口 → GRCS /api/RawOrder/ChangeFloor。
    /// GRCS 地址与场景名从设置取（地图信息页保存）。成功/失败写入系统通知（同 key 合并，实时刷新计数）。</summary>
    [HttpPost("move/dispatch")]
    public async Task<ActionResult<object>> MoveDispatch([FromBody] VehicleOrderRequest order)
    {
        if (string.IsNullOrWhiteSpace(order?.OrderId)) return Ok(new { success = false, code = 0, json = "缺少订单 OrderId" });
        order.SceneName = _settings.Get().SceneName;
        var station = order.StationCodes?.FirstOrDefault() ?? "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, code, json) = await _grcs.SendVehicleOrderAsync(_settings.Get().GrcsBaseUrl, order);
        sw.Stop();
        var elapsedMs = sw.ElapsedMilliseconds;
        var bodyJson = JsonSerializer.Serialize(order, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (ok)
        {
            Interlocked.Increment(ref _moveOkCount);
            _logs.AddOrUpdate("[纯移动·成功]", $"纯移动任务：下发成功，目的地: {station} ✓ 任务号：（{order.OrderId}）（耗时 {elapsedMs}ms）\n请求体: {bodyJson}", "#4ade80");
        }
        else
        {
            Interlocked.Increment(ref _moveFailCount);
            _logs.AddOrUpdate("[纯移动·失败]", $"纯移动任务：下发失败 HTTP {code} · {station}（{order.OrderId}）（耗时 {elapsedMs}ms），等待下轮重试\n请求体: {bodyJson}\nGRCS 响应: {json}", "#f87171");
        }
        return Ok(new { success = ok, code, json, elapsedMs });
    }

    /// <summary>信号自动开关（进入申请/到达/移除/分拣四档，字段可缺省：只改传了的档）。</summary>
    [HttpPost("signals")]
    public ActionResult<object> SetSignals([FromBody] SignalFlagsRequest req)
    {
        if (req.ArrivalAuto.HasValue) _signals.SetArrival(req.ArrivalAuto.Value);
        if (req.RemovalAuto.HasValue) _signals.SetRemoval(req.RemovalAuto.Value);
        if (req.AutoSend.HasValue) _signals.SetSorting(req.AutoSend.Value);
        return Ok(new { success = true });
    }
}

public class IntervalRequest { public int Interval { get; set; } } // 秒
public class StartRequest { public string? TabId { get; set; } public List<string>? TemplateIds { get; set; } }
public class ExecuteRequest { public string? TemplateId { get; set; } public int Count { get; set; } = 1; public int Interval { get; set; } = 0; public string? TabId { get; set; } } // 秒
public class TabRequest { public string? TabId { get; set; } }
public class SignalFlagsRequest
{
    public bool? AdmittanceAuto { get; set; }
    public bool? ArrivalAuto { get; set; }
    public bool? RemovalAuto { get; set; }
    public bool? AutoSend { get; set; }
}
