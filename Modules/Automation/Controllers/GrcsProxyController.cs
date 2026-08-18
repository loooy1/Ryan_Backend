using GrcsBackend.Modules.Automation.Models;
using GrcsBackend.Modules.Automation.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Automation.Controllers;

/// <summary>
/// GRCS 对接代理（/api/wcs/grcs/*，供 WCS 前端调用，后端代发 GRCS 8224）。
/// 前端不再直连 GRCS：GRCS 地址与场景名一律从 WcsSettingsService 读（前端在地图信息页保存，
/// 未保存用默认 localhost:8224 / Show），payload 中的 Warehouse/SceneName 用设置值覆盖，
/// 保证「配置单一数据源」。非 map 接口统一返回 { ok, code, json }，map 返回 zip 字节流。
/// </summary>
[ApiController]
[Route("api/wcs/grcs")]
public class GrcsProxyController : ControllerBase
{
    private readonly GrcsHttpClient _grcs;
    private readonly WcsSettingsService _settings;

    public GrcsProxyController(GrcsHttpClient grcs, WcsSettingsService settings)
    {
        _grcs = grcs;
        _settings = settings;
    }

    private string BaseUrl => _settings.Get().GrcsBaseUrl;
    private string SceneName => _settings.Get().SceneName;

    /// <summary>任务组下发代理（GRCS /api/v1/task_receive）。</summary>
    [HttpPost("task-receive")]
    public async Task<ActionResult<object>> TaskReceive([FromBody] WcsTaskGroup payload)
    {
        payload.Warehouse = SceneName;
        var (ok, code, json) = await _grcs.SendTaskGroupAsync(BaseUrl, payload);
        return Ok(new { ok, code, json });
    }

    /// <summary>车辆任务代理（GRCS /api/RawOrder/ChangeFloor）。</summary>
    [HttpPost("change-floor")]
    public async Task<ActionResult<object>> ChangeFloor([FromBody] VehicleOrderRequest payload)
    {
        payload.SceneName = SceneName;
        var (ok, code, json) = await _grcs.SendVehicleOrderAsync(BaseUrl, payload);
        return Ok(new { ok, code, json });
    }

    /// <summary>库存查询代理（GRCS /api/Cargo，场景按设置）。</summary>
    [HttpGet("cargo")]
    public async Task<ActionResult<object>> Cargo([FromQuery] string? code, [FromQuery] string? locked,
        [FromQuery] int pageNo = 1, [FromQuery] int pageSize = 2000)
    {
        var (ok, code2, json) = await _grcs.QueryCargoInventoryAsync(BaseUrl, SceneName, code, locked, pageNo, pageSize);
        return Ok(new { ok, code = code2, json });
    }

    /// <summary>模拟生成容器入库代理（GRCS /AutoContainerEnter，场景按设置）。</summary>
    [HttpGet("auto-container-enter")]
    public async Task<ActionResult<object>> AutoContainerEnter([FromQuery] string prefix = "container",
        [FromQuery] int num = -1, [FromQuery] int floor = -1, [FromQuery] int type = 1)
    {
        var (ok, code, json) = await _grcs.AutoContainerEnterAsync(BaseUrl, SceneName, prefix, num, floor, type);
        return Ok(new { ok, code, json });
    }

    /// <summary>货物到达通知代理（GRCS /api/v1/container_ready）。</summary>
    [HttpPost("container-ready")]
    public async Task<ActionResult<object>> ContainerReady([FromBody] ContainerReadyDto req)
    {
        req.Warehouse = SceneName;
        var (ok, code, json) = await _grcs.SendContainerReadyAsync(BaseUrl, req);
        return Ok(new { ok, code, json });
    }

    /// <summary>货物移除通知代理（GRCS /api/v1/container_remove）。</summary>
    [HttpPost("container-remove")]
    public async Task<ActionResult<object>> ContainerRemove([FromBody] ContainerRemoveDto req)
    {
        req.Warehouse = SceneName;
        var (ok, code, json) = await _grcs.SendContainerRemoveAsync(BaseUrl, req);
        return Ok(new { ok, code, json });
    }

    /// <summary>分拣完成通知代理（GRCS /api/v1/container_operation_finish）。</summary>
    [HttpPost("operation-finish")]
    public async Task<ActionResult<object>> OperationFinish([FromBody] OperationFinishDto req)
    {
        req.Warehouse = SceneName;
        var (ok, code, json) = await _grcs.SendOperationFinishAsync(BaseUrl, req);
        return Ok(new { ok, code, json });
    }

    /// <summary>地图 zip 下载代理（GRCS /api/Map/GetMap，场景按设置），成功返回字节流。</summary>
    [HttpGet("map")]
    public async Task<ActionResult> Map()
    {
        var (ok, code, bytes, error) = await _grcs.GetMapBytesAsync(BaseUrl, SceneName);
        if (!ok) return Ok(new { ok = false, code, json = error });
        return File(bytes, "application/octet-stream", "map.zip");
    }
}

public class ContainerReadyDto
{
    public DateTime MsgTime { get; set; } = DateTime.Now;
    public string Warehouse { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public string StationCode { get; set; } = "";
}

public class ContainerRemoveDto
{
    public DateTime MsgTime { get; set; } = DateTime.Now;
    public string Warehouse { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public string StationCode { get; set; } = "";
}

public class OperationFinishDto
{
    public DateTime MsgTime { get; set; } = DateTime.Now;
    public string Warehouse { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public bool RemoveContainer { get; set; }
    public string StationCode { get; set; } = "";
    public string AreaCode { get; set; } = "";
}