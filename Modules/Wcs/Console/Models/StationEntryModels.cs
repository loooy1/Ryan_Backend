namespace GrcsBackend.Modules.Wcs.Console.Models;

/// <summary>
/// 统一的外围作业响应（GRCS ResponseMessage 模型：MsgTime + Success + Message）。
/// </summary>
public class WcsResponseModel
{
    public DateTime MsgTime { get; set; } = DateTime.Now;
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
