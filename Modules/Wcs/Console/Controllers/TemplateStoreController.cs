using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Modules.Wcs.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 任务类型模板 + 功能模板存储接口（/api/wcs/templates、/api/wcs/modules）。
/// 前端在任务下发页/信号交互页创建模板后 POST 持久化到 SQLite，跨浏览器共享；
/// 其他页面/设备 GET 拉取同一份（替换 localStorage 单机存储）。
/// </summary>
[ApiController]
[Route("api/wcs/templates")]
public class TaskTemplateController : ControllerBase
{
    private readonly TaskTemplateStore _store;

    public TaskTemplateController(TaskTemplateStore store) => _store = store;

    /// <summary>全部任务类型模板。</summary>
    [HttpGet]
    public ActionResult<object> GetAll() => Ok(new { success = true, items = _store.GetAll() });

    /// <summary>整体替换任务类型模板列表（前端保存整个自定义集）。</summary>
    [HttpPost]
    public ActionResult<object> ReplaceAll([FromBody] List<TaskTemplateDto> items)
    {
        _store.ReplaceAll(items ?? []);
        return Ok(new { success = true, count = _store.GetAll().Count });
    }

    /// <summary>按 Value 删除一条任务类型模板。</summary>
    [HttpDelete("{value}")]
    public ActionResult<object> Remove(string value)
    {
        var ok = _store.Remove(value);
        return Ok(new { success = ok, value });
    }
}

/// <summary>
/// 功能模板存储接口（/api/wcs/modules）。
/// </summary>
[ApiController]
[Route("api/wcs/modules")]
public class FeatureModuleController : ControllerBase
{
    private readonly FeatureModuleStore _store;
    private readonly ModuleExecLogStore _execLog;

    public FeatureModuleController(FeatureModuleStore store, ModuleExecLogStore execLog)
    {
        _store = store;
        _execLog = execLog;
    }

    /// <summary>全部功能模板。</summary>
    [HttpGet]
    public ActionResult<object> GetAll() => Ok(new { success = true, items = _store.GetAll() });

    /// <summary>整体替换功能模板列表（前端保存整个自定义集）。</summary>
    [HttpPost]
    public ActionResult<object> ReplaceAll([FromBody] List<FeatureModuleDto> items)
    {
        _store.ReplaceAll(items ?? []);
        return Ok(new { success = true, count = _store.GetAll().Count });
    }

    /// <summary>按 Id 删除一条功能模板。</summary>
    [HttpDelete("{id}")]
    public ActionResult<object> Remove(string id)
    {
        var ok = _store.Remove(id);
        return Ok(new { success = ok, id });
    }

    /// <summary>模块执行记录（sinceId &gt; 0 只返回新条目；不带返回最近 500 条，Id 最大值为水位）。所有任务（手动 + 自动化）的三类模块统一在后端执行，结果进此处。</summary>
    [HttpGet("logs")]
    public ActionResult<object> ModuleExecLogs([FromQuery] long? sinceId)
    {
        var entries = sinceId is > 0 ? _execLog.GetSince(sinceId.Value) : _execLog.GetSince(0);
        return Ok(new { maxId = _execLog.MaxId, entries });
    }

    /// <summary>清空模块执行记录（内存环形缓冲）。</summary>
    [HttpDelete("logs")]
    public ActionResult<object> ClearModuleExecLogs() { _execLog.Clear(); return Ok(new { success = true }); }
}