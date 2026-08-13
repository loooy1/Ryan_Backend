using GrcsBackend.Modules.Wcs.Models;
using GrcsBackend.Modules.Wcs.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Controllers;

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

    public WcsConsoleController(IAdmittanceService admittance, ITaskStageService stages)
    {
        _admittance = admittance;
        _stages = stages;
    }

    /// <summary>当前状态：是否自动模式 + 待确认数。</summary>
    [HttpGet("status")]
    public ActionResult<object> Status()
    {
        return Ok(new { autoMode = _admittance.IsAutoMode, pendingCount = _admittance.PendingCount });
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

    /// <summary>任务阶段变化事件列表（前端轮询，按时间正序）。
    /// 带 sinceId 时只返回 Id 更大的增量事件；不带则返回最近 200 条。</summary>
    [HttpGet("task-stages")]
    public ActionResult<List<StageChangeEvent>> TaskStages([FromQuery] long? sinceId)
    {
        return Ok(sinceId.HasValue && sinceId.Value > 0
            ? _stages.GetEventsSince(sinceId.Value)
            : _stages.GetEvents());
    }

    /// <summary>删除指定任务的所有阶段事件。</summary>
    [HttpDelete("task-stages/{taskId}")]
    public ActionResult<object> DeleteTaskStages(string taskId)
    {
        _stages.RemoveByTaskId(taskId);
        return Ok(new { success = true, taskId });
    }

    /// <summary>切换准入模式：auto=true 全自动放行，false 手动确认。</summary>
    [HttpPost("mode")]
    public ActionResult<object> SetMode([FromBody] ModeRequest request)
    {
        _admittance.SetAutoMode(request.Auto);
        return Ok(new { success = true, autoMode = _admittance.IsAutoMode });
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
