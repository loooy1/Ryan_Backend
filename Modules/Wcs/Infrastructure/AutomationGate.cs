namespace GrcsBackend.Modules.Wcs.Infrastructure;

/// <summary>
/// 自动化互斥闸（进程内硬互斥 + 跨标签页协调，Skill E 补强）：
/// 轮询自动化（AutoRunHostedService）、批量任务（ContainerTaskRunner）、
/// 纯移动任务循环（前端 MOVE_ONLY 循环下发）共享同一批车辆/储位/接驳位资源，
/// 必须互斥运行——任意一个在执行时，其他一律拒绝启动，杜绝多标签页并发下发。
/// 移动任务循环在前端页面内运行，通过「租约」（tabId + 心跳）向本闸登记：
/// 属主每 5s 心跳续约，超过 TTL（20s）未续约视为属主已关闭，其他标签页可接管。
/// 三个模式均记录发起者 tabId，供前端区分「本页启动」vs「其他标签页启动」。
/// </summary>
public class AutomationGate
{
    private readonly object _lock = new();
    private const double MoveTtlSeconds = 20;

    public bool AutoRunning { get; private set; }
    public bool BatchBusy { get; private set; }

    /// <summary>各模式发起者标签页 id（空 = 老版本启动，无主）。</summary>
    public string? AutoTabId { get; private set; }
    public string? BatchTabId { get; private set; }
    public string? MoveTabId { get; private set; }
    public DateTime MoveBeat { get; private set; }

    public bool MoveRunning => MoveTabId != null && (DateTime.UtcNow - MoveBeat).TotalSeconds < MoveTtlSeconds;

    /// <summary>是否任一模式下发中（前端警示/禁用判断用）。</summary>
    public bool AnyRunning => AutoRunning || BatchBusy || MoveRunning;

    /// <summary>尝试启动轮询自动化（批量执行中或移动循环运行中则拒绝）。</summary>
    public bool TryStartAuto(string? tabId)
    {
        lock (_lock)
        {
            if (BatchBusy || MoveRunning) return false;
            AutoRunning = true;
            AutoTabId = tabId;
            return true;
        }
    }

    public void StopAuto()
    {
        lock (_lock) { AutoRunning = false; AutoTabId = null; }
    }

    /// <summary>尝试启动批量执行（轮询运行中或移动循环运行中则拒绝）。</summary>
    public bool TryStartBatch(string? tabId)
    {
        lock (_lock)
        {
            if (AutoRunning || MoveRunning) return false;
            BatchBusy = true;
            BatchTabId = tabId;
            return true;
        }
    }

    public void StopBatch()
    {
        lock (_lock) { BatchBusy = false; BatchTabId = null; }
    }

    /// <summary>尝试登记移动任务循环租约（轮询/批量运行中，或另一活跃属主持有则拒绝）。</summary>
    public (bool Ok, string? Reason) TryStartMove(string tabId)
    {
        lock (_lock)
        {
            if (AutoRunning) return (false, "轮询自动化正在运行中");
            if (BatchBusy) return (false, "批量任务正在执行中");
            if (MoveRunning && MoveTabId != tabId) return (false, "另一标签页正在循环下发移动任务");
            MoveTabId = tabId;
            MoveBeat = DateTime.UtcNow;
            return (true, null);
        }
    }

    /// <summary>属主续约心跳；非属主或已过期返回 false。</summary>
    public bool BeatMove(string tabId)
    {
        lock (_lock)
        {
            if (MoveTabId == null || MoveTabId != tabId) return false;
            MoveBeat = DateTime.UtcNow;
            return true;
        }
    }

    public void StopMove(string tabId)
    {
        lock (_lock) { if (MoveTabId == tabId) { MoveTabId = null; MoveBeat = default; } }
    }
}