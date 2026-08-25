using System.Collections.Concurrent;

namespace GrcsBackend.Modules.Wcs.Infrastructure;

/// <summary>
/// 模块执行记录（内存环形缓冲，上限 500 条，自增 Id + sinceId 增量拉取）。
/// 取代前端 ModuleRunnerService 的本地日志：所有任务（手动 + 自动化）的模块执行统一在后端发生，
/// 前端「模块执行记录」面板经 /api/wcs/modules/logs 轮询本存储。
/// </summary>
public class ModuleExecLogStore
{
    private readonly object _lock = new();
    private readonly List<ModuleExecLogEntry> _logs = [];
    private long _nextId = 1;
    private const int Max = 500;

    public long Add(string taskId, string point, string module, bool ok, int httpCode, string detail)
    {
        lock (_lock)
        {
            _logs.Add(new ModuleExecLogEntry
            {
                Id = _nextId++,
                Time = DateTime.Now.ToString("HH:mm:ss"),
                TaskId = taskId,
                Point = point,
                Module = module,
                Ok = ok,
                HttpCode = httpCode,
                Detail = detail.Length > 400 ? detail[..400] : detail,
            });
            if (_logs.Count > Max) _logs.RemoveRange(0, _logs.Count - Max);
            return _logs[^1].Id;
        }
    }

    public List<ModuleExecLogEntry> GetSince(long sinceId)
    {
        lock (_lock) return _logs.Where(l => l.Id > sinceId).ToList();
    }

    public void Clear()
    {
        lock (_lock) _logs.Clear();
    }

    public long MaxId { get { lock (_lock) { return _nextId - 1; } } }
}

public class ModuleExecLogEntry
{
    public long Id { get; set; }
    public string Time { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Point { get; set; } = "";
    public string Module { get; set; } = "";
    public bool Ok { get; set; }
    public int HttpCode { get; set; }
    public string Detail { get; set; } = "";
}
