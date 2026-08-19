using System.Collections.Concurrent;
using GrcsBackend.Modules.Wcs.Automation.Services;
using GrcsBackend.Modules.Wcs.Console.Models;

namespace GrcsBackend.Modules.Wcs.Console.Services;

/// <summary>
/// 进入接驳位/进站准入策略。
///
/// ── 两种模式 ──
/// - 自动模式：所有申请直接放行（纯模拟，不打断 GRCS 流程）；
/// - 手动模式：申请先记事件并返回 Success=false（GRCS 会循环重发），
///   等待 WCS 前端通过 /api/wcs/decisions 批准后放行。
///
/// ── 事件状态机 ──
/// Pending（手动模式待确认）→ Approved（前端已批准，等 GRCS 下次重试领取）→
/// Allowed / Rejected（GRCS 领取决策时落终态）；自动模式下直接 Allowed。
/// 同车同站（Key = VehicleCode@StationCode）共享一张卡片，GRCS 每重发一次 Attempts+1。
///
/// ── 决策暂存 ──
/// _decisions 保存"前端已批、GRCS 还没来领取"的一次性决策：领取即消费删除，
/// 下次同车同站进站需要重新确认（防旧决策被无限复用）。
/// </summary>
public interface IAdmittanceService
{
    bool IsAutoMode { get; }
    bool AllowEntry(StationEntryRequestModel request);
    void Decide(string key, bool allow);
    void RemoveEvent(string key);
    void ClearAllEvents();
    List<EntryRequestEvent> GetEvents(int limit = 200);
    int PendingCount { get; }
}

public class AdmittanceService : IAdmittanceService
{
    private readonly ILogger<AdmittanceService> _logger;
    private readonly SignalAutoHostedService _signals;

    private readonly object _lock = new();
    private readonly List<EntryRequestEvent> _events = [];
    private readonly ConcurrentDictionary<string, bool> _decisions = new();
    private long _nextId = 1;
    private const int MaxEvents = 500;

    /// <summary>自动放行模式（默认关闭：手动确认，前端批准后放行）。
    /// 统一数据源在 SignalAutoHostedService（/api/wcs/auto/signals 写入，SQLite 持久化）。</summary>
    public bool IsAutoMode => _signals.AdmittanceAuto;

    public int PendingCount
    {
        get { lock (_lock) return _events.Count(e => e.Status == "Pending"); }
    }

    public AdmittanceService(SignalAutoHostedService signals, ILogger<AdmittanceService> logger)
    {
        _signals = signals;
        _logger = logger;
    }

    public bool AllowEntry(StationEntryRequestModel request)
    {
        var key = $"{request.VehicleCode}@{request.StationCode}";

        if (IsAutoMode)
        {
            RecordEvent(key, request, "Allowed", DateTime.Now);
            return true;
        }

        // 手动模式：查询前端决策
        if (_decisions.TryGetValue(key, out bool decision))
        {
            // 一次性决策：消费后立即移除，下次需重新确认
            _decisions.TryRemove(key, out _);
            string resultStatus = decision ? "Allowed" : "Rejected";
            RecordEvent(key, request, resultStatus);
            _logger.LogInformation(
                "进入申请决策已领取: 站 {StationCode} 车 {VehicleCode} → {Result}",
                request.StationCode, request.VehicleCode, resultStatus);
            return decision;
        }

        RecordEvent(key, request, "Pending");
        _logger.LogInformation(
            "进入申请待确认: 站 {StationCode} 车 {VehicleCode} IsLoaded={IsLoaded}（等待 WCS 前端批准）",
            request.StationCode, request.VehicleCode, request.IsLoaded);
        return false;
    }

    public void Decide(string key, bool allow)
    {
        // 写入决策供 GRCS 下次重试时领取；事件状态标记为 Approved 表示"已批准，等待 GRCS 领取"
        _decisions[key] = allow;
        lock (_lock)
        {
            var evt = _events.LastOrDefault(e => e.Key == key);
            if (evt != null)
            {
                if (evt.Status == "Pending")
                    evt.Status = "Approved";
                evt.DecidedAt = DateTime.Now;
            }
        }
        _logger.LogInformation("WCS 前端决策 {Decision}: {Key}（等待 GRCS 下次重试时领取）", allow ? "批准" : "拒绝", key);
    }

    public void RemoveEvent(string key)
    {
        lock (_lock) { _events.RemoveAll(e => e.Key == key); }
        _decisions.TryRemove(key, out _);
    }

    public void ClearAllEvents()
    {
        lock (_lock) { _events.Clear(); }
        _decisions.Clear();
    }

    public List<EntryRequestEvent> GetEvents(int limit = 200)
    {
        lock (_lock)
        {
            return _events.TakeLast(limit).ToList();
        }
    }

    private void RecordEvent(string key, StationEntryRequestModel request, string status, DateTime? decidedAt = null)
    {
        lock (_lock)
        {
            var existing = _events.FirstOrDefault(e => e.Key == key);
            if (existing != null)
            {
                // 同一台车：更新站点/载货/时间/状态，累加请求计数
                existing.StationCode = request.StationCode;
                existing.IsLoaded = request.IsLoaded;
                existing.Time = DateTime.Now;
                existing.Attempts++;
                existing.Status = status;
                existing.DecidedAt = decidedAt ?? existing.DecidedAt;
                return;
            }
            _events.Add(new EntryRequestEvent
            {
                Id = _nextId++,
                Key = key,
                VehicleCode = request.VehicleCode,
                StationCode = request.StationCode,
                IsLoaded = request.IsLoaded,
                Time = DateTime.Now,
                DecidedAt = decidedAt,
                Status = status,
                Attempts = 1
            });
            if (_events.Count > MaxEvents)
                _events.RemoveRange(0, _events.Count - MaxEvents);
        }
    }
}
