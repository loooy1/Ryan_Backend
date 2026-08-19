using GrcsBackend.Modules.Wcs.Automation.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 信号确认状态接口（/api/wcs/signal-confirm）：
/// GET 全量（前端 1s 轮询跨标签页同步）；POST 抢占（claimed=true 才可发 WCS 信号，防多标签页重复发送）；
/// DELETE 撤销（发送失败回滚 / 恢复）。
/// kind 枚举：arrival / removal / sent。
/// </summary>
[ApiController]
[Route("api/wcs/signal-confirm")]
public class SignalConfirmController : ControllerBase
{
    private readonly SignalConfirmStore _store;

    public SignalConfirmController(SignalConfirmStore store) => _store = store;

    [HttpGet]
    public ActionResult<object> GetAll() => Ok(_store.GetAll());

    [HttpPost("{kind}/{taskId}")]
    public ActionResult<object> Claim(string kind, string taskId, [FromBody] ClaimBody? body)
    {
        if (!ValidKind(kind)) return BadRequest(new { success = false, message = "kind 必须是 arrival/removal/sent" });
        if (string.IsNullOrWhiteSpace(taskId)) return BadRequest(new { success = false, message = "taskId 不能为空" });
        var claimed = _store.Set(kind, taskId.Trim(), body?.Value);
        return Ok(new { claimed, kind, taskId });
    }

    [HttpDelete("{kind}/{taskId}")]
    public ActionResult<object> Remove(string kind, string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return BadRequest(new { success = false, message = "taskId 不能为空" });
        _store.Remove(kind, taskId.Trim());
        return Ok(new { success = true, kind, taskId });
    }

    private static bool ValidKind(string kind) => kind is "arrival" or "removal" or "sent";
}

public class ClaimBody
{
    /// <summary>分拣 sent 的编辑参数 JSON（returnTaskId/removeContainer/destStation/destArea）。</summary>
    public string? Value { get; set; }
}