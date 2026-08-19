using GrcsBackend.Modules.Wcs.Automation.Services;
using GrcsBackend.Modules.Wcs.Console.Models;
using GrcsBackend.Modules.Wcs.Console.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// WCS 控制台接口（供 WCS 前端调用，不是 GRCS 协议接口）。
/// 前端轮询事件、批准/拒绝进入申请、切换自动放行模式。
/// </summary>
[ApiController]
[Route("api/wcs")]
public class WcsConsoleController : ControllerBase
{
    private readonly IAdmittanceService _admittance;
    private readonly ITaskStageService _stages;
    private readonly SignalAutoHostedService _signals;

    public WcsConsoleController(IAdmittanceService admittance, ITaskStageService stages, SignalAutoHostedService signals)
    {
        _admittance = admittance;
        _stages = stages;
        _signals = signals;
    }

    /// <summary>当前状态：是否自动模式 + 待确认数（自动模式读统一信号源 SignalAutoHostedService）。</summary>
    [HttpGet("status")]
    public ActionResult<object> Status()
    {
        return Ok(new { autoMode = _signals.AdmittanceAuto, pendingCount = _admittance.PendingCount });
    }

    /// <summary>进入申请事件列表（前端轮询，按时间正序）。</summary>
    [HttpGet("events")]
    public ActionResult<List<EntryRequestEvent>> Events()
    {
        return Ok(_admittance.GetEvents());
    }

    /// <summary>批准/拒绝一个进入申请（key 为 VehicleCode@StationCode）。</summary>
    [HttpPost("decisions/{key}")]
    public ActionResult<object> Decide(string key, [FromBody] DecisionRequest request)
    {
        _admittance.Decide(key, request.Allow);
        return Ok(new { success = true, key, allow = request.Allow });
    }

    /// <summary>删除指定任务的全部记录（创建行 + 阶段事件，task_records 全行）。</summary>
    [HttpDelete("task-stages/{taskId}")]
    public ActionResult<object> DeleteTaskStages(string taskId)
    {
        _stages.RemoveByTaskId(taskId);
        return Ok(new { success = true, taskId });
    }

    /// <summary>删除单个进入申请事件。</summary>
    [HttpDelete("events/{key}")]
    public ActionResult<object> DeleteEvent(string key)
    {
        _admittance.RemoveEvent(key);
        return Ok(new { success = true, key });
    }

    /// <summary>清空全部进入申请事件。</summary>
    [HttpDelete("events")]
    public ActionResult<object> ClearEvents()
    {
        _admittance.ClearAllEvents();
        return Ok(new { success = true });
    }
}
