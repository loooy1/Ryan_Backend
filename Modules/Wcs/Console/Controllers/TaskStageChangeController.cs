using GrcsBackend.Modules.Wcs.Console.Models;
using GrcsBackend.Modules.Wcs.Console.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 任务阶段变化通知接口（task_stage_change）。
/// GRCS 在任务推进时上报阶段（START / LOAD_FINISH / FINISHED），
/// WCS 后端记录事件供前端查询展示，并立即返回 Success=true（GRCS 不重试）。
/// </summary>
[ApiController]
[Route("api/v1")]
public class TaskStageChangeController : ControllerBase
{
    private readonly ITaskStageService _stages;
    private readonly ILogger<TaskStageChangeController> _logger;

    public TaskStageChangeController(ITaskStageService stages, ILogger<TaskStageChangeController> logger)
    {
        _stages = stages;
        _logger = logger;
    }

    [HttpPost("task_stage_change")]
    public ActionResult<WcsResponseModel> TaskStageChange([FromBody] TaskStageChangeModel request)
    {
        _stages.Record(request);
        _logger.LogInformation(
            "任务阶段变化: 任务 {TaskId} 阶段 {Stage} 容器 {ContainerCode} 站点 {StationCode}",
            request.TaskId, request.Stage, request.ContainerCode, request.StationCode);
        return Ok(new WcsResponseModel
        {
            Success = true,
            Message = "Callback processed"
        });
    }
}
