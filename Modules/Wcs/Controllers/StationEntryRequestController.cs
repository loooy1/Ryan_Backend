using GrcsBackend.Modules.Wcs.Models;
using GrcsBackend.Modules.Wcs.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Controllers;

/// <summary>
/// 接驳位/站点进入申请接口。
/// GRCS 在车辆到达前循环 POST 此接口（station_entry_request），
/// 直到响应 Success=true 才放行车辆进入。响应 MsgTime 必须为 yyyy-MM-dd HH:mm:ss.fff 格式。
/// </summary>
[ApiController]
[Route("api/v1")]
public class StationEntryRequestController : ControllerBase
{
    private readonly IAdmittanceService _admittance;

    public StationEntryRequestController(IAdmittanceService admittance)
    {
        _admittance = admittance;
    }

    [HttpPost("station_entry_request")]
    public ActionResult<WcsResponseModel> StationEntryRequest([FromBody] StationEntryRequestModel request)
    {
        bool allow = _admittance.AllowEntry(request);
        return Ok(new WcsResponseModel
        {
            Success = allow,
            Message = allow ? "允许进入" : "拒绝进入"
        });
    }
}
