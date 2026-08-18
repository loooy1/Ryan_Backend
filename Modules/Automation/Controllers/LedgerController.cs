using GrcsBackend.Modules.Automation.Models;
using GrcsBackend.Modules.Automation.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Automation.Controllers;

/// <summary>任务台账接口（/api/wcs/ledger/*）：查询/清空（SQLite 持久化，上限 2000 条）。</summary>
[ApiController]
[Route("api/wcs/ledger")]
public class LedgerController : ControllerBase
{
    private readonly LedgerStore _ledger;

    public LedgerController(LedgerStore ledger) => _ledger = ledger;

    [HttpGet]
    public ActionResult<List<TaskLedgerEntry>> Get([FromQuery] int limit = 500)
        => Ok(_ledger.Get(limit));

    /// <summary>追加条目（手动任务下发页等前端写入；后端 SQLite 持久化，上限 2000 条）。</summary>
    [HttpPost]
    public ActionResult<object> Append([FromBody] List<TaskLedgerEntry> entries)
    {
        _ledger.AppendAsync(entries);
        return Ok(new { success = true, count = entries.Count });
    }

    [HttpDelete]
    public ActionResult<object> Clear()
    {
        _ledger.Clear();
        return Ok(new { success = true });
    }
}
