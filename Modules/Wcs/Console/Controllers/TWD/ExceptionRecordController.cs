using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Modules.Wcs.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Console.Controllers.TWD;

/// <summary>
/// 异常记录接口（/api/wcs/exception-records）：AGV/软件异常台账。
/// 纯 HTTP（前端推给后端 / 从后端读取），无轮询无 SignalR。
/// GET 列表（发生时间倒序）；POST 新增；PUT 更新；DELETE 删除；
/// POST /{id}/reproduce 复现三联动（次数 +1、复现时间=当前、自动置为未解决）。
/// </summary>
[ApiController]
[Route("api/wcs/exception-records")]
public class ExceptionRecordController : ControllerBase
{
    private readonly ExceptionRecordStore _store;

    public ExceptionRecordController(ExceptionRecordStore store) => _store = store;

    /// <summary>查询参数：vehicle=车号关键字（LIKE 模糊，匹配历史车号任意一个）；dateFrom/dateTo=发生日期(yyyy-MM-dd，可含时间)；resolved=0/1 是否解决；dept=责任部门（精确匹配 RCS/WCS/Quicktron）。</summary>
    [HttpGet]
    public ActionResult<object> GetAll(string? vehicle = null, string? dateFrom = null, string? dateTo = null, int? resolved = null, string? dept = null)
        => Ok(_store.GetAll(vehicle, dateFrom, dateTo, resolved.HasValue ? resolved.Value != 0 : null, dept));

    [HttpPost]
    public ActionResult<object> Add([FromBody] ExceptionRecordDto rec)
    {
        if (rec == null) return BadRequest(new { success = false, message = "请求体不能为空" });
        if (string.IsNullOrWhiteSpace(rec.HappenedAt)) return BadRequest(new { success = false, message = "发生时间不能为空" });
        if (string.IsNullOrWhiteSpace(rec.Phenomenon)) return BadRequest(new { success = false, message = "现象不能为空" });
        if (string.IsNullOrWhiteSpace(rec.ResponsibleDept)) return BadRequest(new { success = false, message = "责任部门不能为空（RCS/WCS/Quicktron）" });
        var id = _store.Add(rec);
        return Ok(new { success = true, id });
    }

    [HttpPut("{id:long}")]
    public ActionResult<object> Update(long id, [FromBody] ExceptionRecordDto rec)
    {
        if (rec == null) return BadRequest(new { success = false, message = "请求体不能为空" });
        if (string.IsNullOrWhiteSpace(rec.HappenedAt)) return BadRequest(new { success = false, message = "发生时间不能为空" });
        if (string.IsNullOrWhiteSpace(rec.ResponsibleDept)) return BadRequest(new { success = false, message = "责任部门不能为空（RCS/WCS/Quicktron）" });
        rec.Id = id;
        _store.Update(rec);
        return Ok(new { success = true, id });
    }

    /// <summary>复现三联动：次数 +1、复现时间=当前时间、自动置为未解决；body.VehicleCode 覆盖车号（空=清空）。</summary>
    [HttpPost("{id:long}/reproduce")]
    public ActionResult<object> Reproduce(long id, [FromBody] ExceptionRecordReproduceRequest req)
    {
        _store.Reproduce(id, req?.VehicleCode);
        return Ok(new { success = true, id });
    }

    [HttpDelete("{id:long}")]
    public ActionResult<object> Delete(long id)
    {
        _store.Remove(id);
        return Ok(new { success = true, id });
    }
}