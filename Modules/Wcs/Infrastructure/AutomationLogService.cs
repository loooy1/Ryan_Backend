using GrcsBackend.Modules.Wcs.Infrastructure.Models;

namespace GrcsBackend.Modules.Wcs.Infrastructure;

/// <summary>
/// 自动化/批量执行日志（内存环形缓冲，上限 500 条，带自增 Id）。
/// 前端以 sinceId 增量拉取（与 task-stages 同一收敛模式）；进程重启后 Id 回绕，
/// 前端壳发现服务端最大 Id 小于水位时整表替换。
/// </summary>
public class AutomationLogService
{
    private readonly object _lock = new();
    private readonly List<LogEntryDto> _logs = [];
    private long _nextId = 1;
    private const int Max = 500;

    public long Add(string message, string color = "#94a3b8")
    {
        lock (_lock)
        {
            _logs.Add(new LogEntryDto { Id = _nextId++, Time = DateTime.Now.ToString("HH:mm:ss"), Message = message, Color = color });
            if (_logs.Count > Max) _logs.RemoveRange(0, _logs.Count - Max);
            return _logs[^1].Id;
        }
    }

    public List<LogEntryDto> GetSince(long sinceId)
    {
        lock (_lock)
        {
            return _logs.Where(l => l.Id > sinceId).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock) { _logs.Clear(); }
    }

    public long MaxId { get { lock (_lock) { return _nextId - 1; } } }
}
