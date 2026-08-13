using GrcsBackend.Modules.Wcs.Models;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Controllers;

/// <summary>
/// 站点离开确认接口（station_departure_confirmed）。
/// GRCS 在车辆完成取/卸货离开站点后回调，通知 WCS 车辆已离站。
/// 当前只做日志记录，始终返回 Success=true 避免 GRCS 重试。
/// </summary>
[ApiController]
[Route("api/v1")]
public class StationDepartureConfirmedController : ControllerBase
{
    private readonly ILogger<StationDepartureConfirmedController> _logger;

    public StationDepartureConfirmedController(ILogger<StationDepartureConfirmedController> logger)
    {
        _logger = logger;
    }

    [HttpPost("station_departure_confirmed")]
    public ActionResult<WcsResponseModel> StationDepartureConfirmed([FromBody] StationDepartureConfirmedModel request)
    {
        _logger.LogInformation(
            "站点离开确认: 车 {Vehicle} 站 {Station} 任务 {TaskId}",
            request.VehicleCode, request.StationCode, request.TaskId);
        return Ok(new WcsResponseModel
        {
            Success = true,
            Message = "OK"
        });
    }
}
