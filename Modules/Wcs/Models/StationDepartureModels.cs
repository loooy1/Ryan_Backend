namespace GrcsBackend.Modules.Wcs.Models;

/// <summary>
/// 站点离开确认请求（GRCS StationDepartureConfirmed 回调）。
/// </summary>
public class StationDepartureConfirmedModel
{
    public string MsgTime { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public string VehicleCode { get; set; } = "";
    public string StationCode { get; set; } = "";
    public string TaskId { get; set; } = "";
}
