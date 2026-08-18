namespace GrcsBackend.Modules.Wcs.Models;

/// <summary>进入申请的决策事件（供 WCS 前端轮询展示与批准）。</summary>
public class EntryRequestEvent
{
    public long Id { get; set; }
    /// <summary>关联键：VehicleCode@StationCode（同车同站多次请求共享一个卡片，累加计数）。</summary>
    public string Key { get; set; } = "";
    public string VehicleCode { get; set; } = "";
    public string StationCode { get; set; } = "";
    public bool IsLoaded { get; set; }
    public DateTime Time { get; set; }
    /// <summary>决策/放行时间（WCS 前端批准/拒绝时刻；自动模式下为自动放行时刻）。</summary>
    public DateTime? DecidedAt { get; set; }
    /// <summary>状态：Pending / Allowed / Rejected。</summary>
    public string Status { get; set; } = "Pending";
    /// <summary>GRCS 循环重试次数（每次重新申请 +1）。</summary>
    public int Attempts { get; set; }
}

/// <summary>批准/拒绝请求体。</summary>
public class DecisionRequest
{
    public bool Allow { get; set; }
}
