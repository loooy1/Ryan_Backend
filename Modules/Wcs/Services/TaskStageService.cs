using GrcsBackend.Modules.Wcs.Models;

namespace GrcsBackend.Modules.Wcs.Services;

/// <summary>
/// 任务阶段事件记录：GRCS 上报 task_stage_change，WCS 前端轮询展示任务进度。
/// 状态必须跨请求共享（GRCS 上报 + 前端查询），用 Singleton 注册。
///
/// ── Id 契约（前端 TaskStageHub 依赖）──
/// 每条事件有一个内存自增 Id（_nextId，单调递增、进程存活期间不回绕），
/// 前端增量轮询以 sinceId 为水位只取新事件。注意：本服务是内存态，
/// 进程重启后 Id 从 1 重新开始——前端 hub 有全量对账机制应对这种回绕
/// （连续多轮增量无结果后强制全量，发现服务端最大 Id 小于水位即整表替换）。
///
/// ── 容量边界 ──
/// MaxEvents 兜底上限：超过时丢弃最旧事件（RemoveRange 前段）。
/// GetEvents 默认只回最近 200 条（全量首拉），GetEventsSince 上限 1000 条
/// （增量场景 1000 足够覆盖活跃任务；前端缓存本身也只保留 1000 条）。
/// </summary>
public interface ITaskStageService
{
    void Record(TaskStageChangeModel change);
    List<StageChangeEvent> GetEvents(int limit = 200);
    /// <summary>增量查询：只返回 Id 大于 sinceId 的事件（前端轮询收敛用）。</summary>
    List<StageChangeEvent> GetEventsSince(long sinceId, int limit = 1000);
    void RemoveByTaskId(string taskId);
}

public class TaskStageService : ITaskStageService
{
    private readonly object _lock = new();
    private readonly List<StageChangeEvent> _events = [];
    private long _nextId = 1;
    private const int MaxEvents = 1000;

    public void Record(TaskStageChangeModel change)
    {
        lock (_lock)
        {
            _events.Add(new StageChangeEvent
            {
                Id = _nextId++,
                TaskId = change.TaskId,
                Warehouse = change.Warehouse,
                StationCode = change.StationCode,
                ContainerCode = change.ContainerCode,
                Stage = change.Stage,
                Time = change.MsgTime
            });
            if (_events.Count > MaxEvents)
                _events.RemoveRange(0, _events.Count - MaxEvents);
        }
    }

    public List<StageChangeEvent> GetEvents(int limit = 200)
    {
        lock (_lock)
        {
            return _events.TakeLast(limit).ToList();
        }
    }

    /// <summary>
    /// 增量查询：只返回 Id 大于 sinceId 的事件，最多 limit 条。
    /// 前端 TaskStageHub 每轮携带上次水位调用，把"每秒全量拉 200 条"降为"只传新增几条"，
    /// 是全应用唯一轮询器（前端 6 处消费方共享）的配套后端能力。
    /// </summary>
    public List<StageChangeEvent> GetEventsSince(long sinceId, int limit = 1000)
    {
        lock (_lock)
        {
            return _events.Where(e => e.Id > sinceId).TakeLast(limit).ToList();
        }
    }

    public void RemoveByTaskId(string taskId)
    {
        lock (_lock)
        {
            _events.RemoveAll(e => e.TaskId == taskId);
        }
    }
}
