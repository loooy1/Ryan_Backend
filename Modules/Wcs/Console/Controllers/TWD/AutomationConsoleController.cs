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
    private readonly MoveLoopRunner _moveLoop;
    private readonly NestRunner _nest;
    private readonly NestConfigService _nestConfig;

    public AutomationConsoleController(AutoTemplateRunner auto, AutoTemplateStore templates, AutomationLogService logs,
        RangeConfigService rangeConfig, WcsSettingsService settings, SignalAutoHostedService signals,
        MoveLoopRunner moveLoop, NestRunner nest, NestConfigService nestConfig)
    {
        _auto = auto;
        _templates = templates;
        _logs = logs;
        _rangeConfig = rangeConfig;
        _settings = settings;
        _signals = signals;
        _moveLoop = moveLoop;
        _nest = nest;
        _nestConfig = nestConfig;
    }

    /// <summary>库存分类汇总 + 明细（纯空托 / 带货托 / 纯货物 / 锁定中=移动单元数），按「以前逻辑」在后端统计。</summary>
    [HttpGet("inventory-summary")]
    public async Task<ActionResult<object>> InventorySummary()
    {
        return Ok(await _auto.GetInventorySummaryAsync());
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
            moveRunning = _moveLoop.Running,
            moveTabId = _moveLoop.TabId,
            moveTotal = _moveLoop.Total,
            moveOk = _moveLoop.Ok,
            moveFail = _moveLoop.Fail,
            moveLastError = _moveLoop.LastError,
            templates = _templates.GetAll(),
            settings = _settings.Get(),
            signals = new { arrivalAuto = _signals.ArrivalAuto, removalAuto = _signals.RemovalAuto, autoSend = _signals.AutoSend },
            nestRunning = _nest.Running,
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

    /// <summary>单条保存（新建或更新），只动当前模板，不影响其他行。</summary>
    [HttpPut("templates/{id}")]
    public ActionResult<object> SaveTemplate([FromRoute] string id, [FromBody] AutoTemplateDto item)
    {
        if (item == null) return BadRequest(new { success = false, errors = new[] { "模板内容为空" } });
        item.Id = id;
        var errors = _auto.ValidateTemplates([item]);
        if (errors.Count > 0)
            return BadRequest(new { success = false, errors });
        _templates.Upsert(item);
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

    /// <summary>纯移动任务循环启动：前端只通知「开启」，循环/选点/统计/日志全在 MoveLoopRunner（后端）。
    /// 互斥（模板轮询/其他标签页）由 AutomationGate 判定。</summary>
    [HttpPost("move/start")]
    public ActionResult<object> MoveStart([FromBody] StartMoveReq req)
    {
        var (ok, reason) = _moveLoop.Start(req ?? new StartMoveReq());
        return Ok(new { success = ok, reason });
    }

    /// <summary>纯移动任务循环停止：取消循环、写汇总日志、释放互斥。</summary>
    [HttpPost("move/stop")]
    public ActionResult<object> MoveStop([FromBody] TabRequest req)
    {
        _moveLoop.Stop(req?.TabId);
        return Ok(new { success = true });
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

    // ── 归巢模式（查询就绪车 → 巢点附近普通路点 → 逐台 MOVE_ONLY 指定车下发）──

    [HttpGet("nest/config")]
    public ActionResult<NestConfigDto> NestConfig() => Ok(_nestConfig.Get());

    [HttpPut("nest/config")]
    public ActionResult<object> SaveNestConfig([FromBody] NestConfigDto config)
    {
        _nestConfig.Set(config ?? new NestConfigDto());
        return Ok(new { success = true });
    }

    /// <summary>执行一次归巢（后台异步批量下发，运行中重复调用被忽略）。</summary>
    [HttpPost("nest/run")]
    public ActionResult<object> NestRun()
    {
        var (ok, reason) = _nest.Run();
        return Ok(new { success = ok, reason });
    }

    [HttpGet("nest/status")]
    public ActionResult<NestStatsDto> NestStatus() => Ok(_nest.Snapshot());
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
