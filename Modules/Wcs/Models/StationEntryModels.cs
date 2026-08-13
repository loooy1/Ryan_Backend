namespace GrcsBackend.Modules.Wcs.Models;

/// <summary>
/// 进站/进入接驳位申请请求（GRCS StationEntryRequest 出站调用）。
/// 字段对齐 GRCS StationEntryRequestExecution.RequestMessage。
/// </summary>
public class StationEntryRequestModel
{
    public DateTime MsgTime { get; set; }
    public string Warehouse { get; set; } = "";
    public string VehicleCode { get; set; } = "";
    public string StationCode { get; set; } = "";
    public bool IsLoaded { get; set; }
}

/// <summary>
/// 统一的外围作业响应（GRCS ResponseMessage 模型：MsgTime + Success + Message）。
/// </summary>
public class WcsResponseModel
{
    public DateTime MsgTime { get; set; } = DateTime.Now;
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
