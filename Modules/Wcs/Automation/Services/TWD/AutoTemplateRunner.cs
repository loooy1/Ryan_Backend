using System.Collections.Concurrent;
using System.Text.Json;
using GrcsBackend.Modules.Wcs.Console.Services;
using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Modules.Wcs.Infrastructure.Models;
using GrcsBackend.Modules.Wcs.Proxy.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrcsBackend.Modules.Wcs.Automation.Services.TWD;

/// <summary>
/// 自动化模板执行引擎（取代旧的硬编码两段式 AutoRunHostedService + ContainerTaskRunner）。
/// 用户在前端「自动化」页创建模板：模板 = 线性有序步骤，每步从
/// 选托盘(PickPallet) / 选货物(PickCargo) / 执行任务模板(RunTemplate) 中选。
/// 执行时按步骤顺序进行：选托盘/选货物从范围内库存快照随机抽取（取出后从池移除，避免同轮复用）；
/// 执行任务模板时用前置步骤挑出的托盘/货物填充容器与起点，并按模板类型选终点，下发单个 GRCS 任务。
/// 模板的「起点模块」在发送前执行、『起点之后模块』在发送成功后执行、『终点模块』在任务 FINISHED 后执行，
/// 三类模块统一经 ModuleRunService（后端执行器）完成，不再依赖前端。
/// 轮询模式：Start 后每 Interval 毫秒执行一次当前模板；单次模式：ExecuteOnce 立即执行 N 次。
/// 通过 AutomationGate 与移动任务循环互斥，单实例串行（同一时刻只跑一个模板），杜绝并发下发。
/// </summary>
public class AutoTemplateRunner : IHostedService
{
    private readonly GrcsHttpClient _grcs;
    private readonly MapStoreService _map;
    private readonly RangeConfigService _range;
    private readonly WcsSettingsService _settings;
    private readonly StationLockStore _locks;
    private readonly AutomationLogService _log;
    private readonly ITaskStageService _stage;
    private readonly AutomationGate _gate;
    private readonly ModuleRunService _modules;
    private readonly TaskTemplateStore _taskTemplates;
    private readonly AutoTemplateStore _templates;
    private readonly MockRuleStore _mocks;
    private readonly ILogger<AutoTemplateRunner> _logger;
    private readonly AutomationDb _db;

    private readonly object _stateLock = new();
    private bool _running;
    private int _interval = 3000;
    private List<string> _activeTemplateIds = new();
    private int _executed;
    private int _roundSeq; // 成功轮数（选托盘/选货物成功后才 +1）
    private string _status = "未启动";
    private string? _activeTabId;
    private CancellationTokenSource? _cts;

    // 运行状态持久化键（kv 表）：重启后自动恢复轮询与进行中任务跟踪
    private const string RunStateKey = "auto_run_state";

    // 任务号 → 锁定的起点 mark（FINISHED 后释放）
    private readonly ConcurrentDictionary<string, string> _lockByTask = new(StringComparer.OrdinalIgnoreCase);

    // 已占用容器（选托盘/选货物后加入，任务 FINISHED 后移除）；跨轮次排除，避免下一轮重复选中
    private readonly object _busyLock = new();
    private readonly HashSet<string> _busyContainers = new(StringComparer.OrdinalIgnoreCase);
    // 任务号 → 锁定的终点 mark（FINISHED 后释放）
    private readonly ConcurrentDictionary<string, string> _endLockByTask = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _containerByTask = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _loadedPalletByTask = new(StringComparer.OrdinalIgnoreCase);

    // 执行链断点（模板 id → 链状态）：多步骤串联模板每步执行前快照，重启后从断点续跑
    private readonly ConcurrentDictionary<string, ChainStateDto> _chains = new(StringComparer.OrdinalIgnoreCase);

    public AutoTemplateRunner(
        GrcsHttpClient grcs, MapStoreService map, RangeConfigService range, WcsSettingsService settings,
        StationLockStore locks, AutomationLogService log, ITaskStageService stage, AutomationGate gate,
        ModuleRunService modules, TaskTemplateStore taskTemplates, AutoTemplateStore templates,
        MockRuleStore mocks, ILogger<AutoTemplateRunner> logger, AutomationDb db)
    {
        _grcs = grcs; _map = map; _range = range; _settings = settings; _locks = locks; _log = log;
        _stage = stage; _gate = gate; _modules = modules; _taskTemplates = taskTemplates;
        _templates = templates; _mocks = mocks; _logger = logger; _db = db;
    }

    // ── 状态 ──
    public bool Running => _running;
    public int Interval => _interval;
    public string? ActiveTemplateId => _activeTemplateIds.FirstOrDefault();
    public string? ActiveTemplateName => string.Join(" + ", _activeTemplateIds.Select(id => _templates.GetAll().FirstOrDefault(t => t.Id == id)?.Name).Where(n => n != null));
    public int Executed => _executed;
    public string Status => _status;
    public string? ActiveTabId => _activeTabId;

    public AutoTemplateStatusDto Snapshot()
        => new()
        {
            Running = _running,
            Interval = _interval,
            ActiveTemplateId = ActiveTemplateId,
            ActiveTemplateName = ActiveTemplateName,
            ActiveTabId = _activeTabId,
            Executed = _executed,
            Status = _status,
            GateAuto = _gate.AutoRunning,
            AnyRunning = _gate.AnyRunning,
        };

    Task IHostedService.StartAsync(CancellationToken ct)
    {
        _ = RestoreAsync();
        return Task.CompletedTask;
    }
    Task IHostedService.StopAsync(CancellationToken ct) { Stop(hostShutdown: true); return Task.CompletedTask; }

    // ── 运行状态持久化（重启自动恢复轮询 + 进行中任务跟踪）──

    private class RunStateDto
    {
        public bool Running { get; set; }
        public List<string>? ActiveTemplateIds { get; set; }
        public string? ActiveTabId { get; set; }
        public int RoundSeq { get; set; }
        public List<string>? BusyContainers { get; set; }
        public Dictionary<string, RunTaskStateDto>? Tasks { get; set; }
        public Dictionary<string, ChainStateDto>? Chains { get; set; }
    }

    private class RunTaskStateDto
    {
        public string? Container { get; set; }
        public string? Pallet { get; set; }
        public string? StartMark { get; set; }
        public string? EndMark { get; set; }
    }

    /// <summary>执行链断点状态（模板 id → 链）：StepIndex = 正在执行的步骤索引；
    /// WaitingTaskId 非空 = 该步骤任务已下发等待 FINISHED（重启后等它完成再续跑下一步）。</summary>
    private class ChainStateDto
    {
        public int StepIndex { get; set; }
        public string? ChildId { get; set; }
        public string? ParentId { get; set; }
        public string? WaitingTaskId { get; set; }
        public ExecCtxDto? Ctx { get; set; }
    }

    private class ExecCtxDto
    {
        public string? PalletCode { get; set; }
        public string? PalletMark { get; set; }
        public string? CargoCode { get; set; }
        public string? CargoMark { get; set; }
        public string? ContainerCode { get; set; }
        public string? LastEndMark { get; set; }
    }

    /// <summary>把当前运行状态（轮询开关/活动模板/轮次/占用容器/进行中任务）快照写入 kv 表，失败静默。</summary>
    private void PersistState()
    {
        try
        {
            RunStateDto dto;
            lock (_stateLock)
            {
                dto = new RunStateDto
                {
                    Running = _running,
                    ActiveTemplateIds = new List<string>(_activeTemplateIds),
                    ActiveTabId = _activeTabId,
                    RoundSeq = _roundSeq,
                };
            }
            lock (_busyLock) dto.BusyContainers = _busyContainers.ToList();
            dto.Tasks = new Dictionary<string, RunTaskStateDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _containerByTask)
            {
                _lockByTask.TryGetValue(kv.Key, out var sm);
                _endLockByTask.TryGetValue(kv.Key, out var em);
                _loadedPalletByTask.TryGetValue(kv.Key, out var pallet);
                dto.Tasks[kv.Key] = new RunTaskStateDto { Container = kv.Value, Pallet = pallet, StartMark = sm, EndMark = em };
            }
            dto.Chains = new Dictionary<string, ChainStateDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, chain) in _chains) dto.Chains[id] = chain;
            _db.KvSet(RunStateKey, JsonSerializer.Serialize(dto, JsonOpts));
        }
        catch { }
    }

    /// <summary>启动时恢复：读 kv 状态 → 恢复占用容器与轮次 → 为进行中任务注册 FINISHED 跟踪 →
    /// 恢复执行链断点（续跑）→ 上次轮询中且模板有效时自动恢复轮询。全部重置（ForceEnd）后状态已清空，不会恢复。</summary>
    private async Task RestoreAsync()
    {
        RunStateDto? dto = null;
        try
        {
            var json = _db.KvGet(RunStateKey);
            if (!string.IsNullOrWhiteSpace(json)) dto = JsonSerializer.Deserialize<RunStateDto>(json, JsonOpts);
        }
        catch { }
        if (dto == null) return;
        _roundSeq = dto.RoundSeq;
        lock (_busyLock)
        {
            if (dto.BusyContainers != null)
                foreach (var c in dto.BusyContainers) if (!string.IsNullOrEmpty(c)) _busyContainers.Add(c);
        }
        var finished = _stage.FinishedTaskIds;
        // 链等待中的任务由链续跑管理（等完成→续跑下一步→释放），跳过任务注册表，避免双等待
        var chainWaiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, ch) in dto.Chains ?? new Dictionary<string, ChainStateDto>())
            if (!string.IsNullOrEmpty(ch?.WaitingTaskId)) chainWaiting.Add(ch.WaitingTaskId!);
        var restored = 0;
        foreach (var (taskId, ts) in dto.Tasks ?? new Dictionary<string, RunTaskStateDto>())
        {
            if (string.IsNullOrEmpty(taskId) || chainWaiting.Contains(taskId)) continue;
            if (finished.Contains(taskId))
            {
                // 重启前已 FINISHED：立即释放锁与占用（_finished 集合由 TaskStageService 从 DB 恢复）
                ReleaseTaskLocks(taskId);
                lock (_busyLock)
                {
                    if (!string.IsNullOrEmpty(ts?.Container)) _busyContainers.Remove(ts.Container);
                    if (!string.IsNullOrEmpty(ts?.Pallet)) _busyContainers.Remove(ts.Pallet);
                }
                continue;
            }
            if (!string.IsNullOrEmpty(ts?.StartMark)) _lockByTask[taskId] = ts.StartMark;
            if (!string.IsNullOrEmpty(ts?.EndMark)) _endLockByTask[taskId] = ts.EndMark;
            if (!string.IsNullOrEmpty(ts?.Container)) _containerByTask[taskId] = ts.Container;
            if (!string.IsNullOrEmpty(ts?.Pallet)) _loadedPalletByTask[taskId] = ts.Pallet;
            restored++;
            _ = ResumeTrackingAsync(taskId);
        }
        if (restored > 0)
            _log.Add($"🔁 重启恢复：{restored} 个进行中任务已恢复跟踪，完成后自动释放占用与站点锁", "#38bdf8");
        // 恢复执行链断点：先续跑链（等断点任务完成 → 从下一步继续），全部续跑完成后再恢复轮询，避免整链重跑与续跑冲突
        var chains = dto.Chains ?? new Dictionary<string, ChainStateDto>();
        if (chains.Count > 0)
        {
            _log.Add($"🔁 重启恢复：{chains.Count} 条执行链断点已恢复，续跑完成后自动继续轮询", "#38bdf8");
            await Task.WhenAll(chains.Select(kv => ResumeChainAsync(kv.Key, kv.Value!)));
        }
        // 恢复轮询
        if (dto.Running && (dto.ActiveTemplateIds?.Count ?? 0) > 0 && _mocks.HasTaskStageRule())
        {
            var all = _templates.GetAll();
            var ids = (dto.ActiveTemplateIds ?? new List<string>()).Where(id => all.Any(t => t.Id == id)).ToList();
            if (ids.Count == (dto.ActiveTemplateIds?.Count ?? 0) && ids.Count > 0)
            {
                if (!_gate.TryStartAuto(dto.ActiveTabId))
                {
                    _log.Add("⚠ 重启恢复：互斥闸被移动任务循环占用，未自动恢复轮询，请手动启动", "#fbbf24");
                    return;
                }
                lock (_stateLock)
                {
                    _activeTemplateIds = ids;
                    _activeTabId = dto.ActiveTabId;
                    _running = true;
                    _status = "轮询中（重启恢复）";
                }
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _log.Add($"🔁 重启恢复：自动化轮询已自动恢复（{ActiveTemplateName}），占用容器已排除，不会重复选中", "#4ade80");
                _ = PollLoop(_cts.Token);
                return;
            }
            _log.Add("⚠ 重启恢复：上次轮询的模板已变更或缺失，未自动恢复，请手动启动", "#fbbf24");
        }
    }

    // ── 执行链断点辅助 ──

    private static ExecCtxDto CtxToDto(ExecCtx ctx)
        => new()
        {
            PalletCode = ctx.PalletCode,
            PalletMark = ctx.PalletMark,
            CargoCode = ctx.CargoCode,
            CargoMark = ctx.CargoMark,
            ContainerCode = ctx.ContainerCode,
            LastEndMark = ctx.LastEndMark,
        };

    private static ExecCtx CtxFromDto(ExecCtxDto? dto)
        => dto == null ? new ExecCtx() : new ExecCtx
        {
            PalletCode = dto.PalletCode,
            PalletMark = dto.PalletMark,
            CargoCode = dto.CargoCode,
            CargoMark = dto.CargoMark,
            ContainerCode = dto.ContainerCode,
            LastEndMark = dto.LastEndMark,
        };

    /// <summary>记录链断点：进入步骤前调用（StepIndex=当前步骤、ctx 快照），保留既有 WaitingTaskId。</summary>
    private void UpsertChain(string tplId, int stepIndex, string? childId, string? parentId, ExecCtx ctx)
    {
        _chains.TryGetValue(tplId, out var existing);
        _chains[tplId] = new ChainStateDto
        {
            StepIndex = stepIndex,
            ChildId = childId,
            ParentId = parentId,
            WaitingTaskId = existing?.WaitingTaskId,
            Ctx = CtxToDto(ctx),
        };
        PersistState();
    }

    private void SetChainWaiting(string tplId, string taskId)
    {
        if (_chains.TryGetValue(tplId, out var c)) c.WaitingTaskId = taskId;
        PersistState();
    }

    private void ClearChainWaiting(string tplId)
    {
        if (_chains.TryGetValue(tplId, out var c)) c.WaitingTaskId = null;
        PersistState();
    }

    private void RemoveChain(string tplId)
    {
        if (_chains.TryRemove(tplId, out _)) PersistState();
    }

    /// <summary>断点续跑：等断点任务 FINISHED（30 分钟超时放弃链）→ 释放其占用/锁 →
    /// 用持久化 ctx 从断点步骤继续执行该模板剩余步骤（段2 使用前置容器，无需库存）。
    /// 续跑新下发的任务注册到任务字典（FINISHED 后由 ReleaseOnFinishAsync 释放占用）。</summary>
    private async Task ResumeChainAsync(string tplId, ChainStateDto chain)
    {
        var tpl = _templates.GetAll().FirstOrDefault(t => t.Id == tplId);
        if (tpl == null) { RemoveChain(tplId); return; }
        var ctx = CtxFromDto(chain.Ctx);
        var waitingId = chain.WaitingTaskId;
        if (!string.IsNullOrEmpty(waitingId))
        {
            if (!_stage.FinishedTaskIds.Contains(waitingId))
            {
                try { await _stage.WaitFinishedAsync(waitingId); }
                catch { }
            }
            // 断点任务已完成：释放其锁与占用（链自己管理）
            ReleaseTaskLocks(waitingId);
            lock (_busyLock)
            {
                if (_containerByTask.TryRemove(waitingId, out var c)) _busyContainers.Remove(c);
                if (_loadedPalletByTask.TryRemove(waitingId, out var p)) _busyContainers.Remove(p);
            }
            chain.WaitingTaskId = null;
        }
        var settings = _settings.Get();
        if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl))
        {
            _log.Add($"⚠ 恢复链「{tpl.Name}」：未配置 GRCS 地址，已放弃该链", "#f87171");
            RemoveChain(tplId);
            return;
        }
        var (emptyPallets, loadedPallets, cargos, _) = await SnapshotAsync(settings, chain.ParentId ?? "");
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in emptyPallets.Concat(loadedPallets).Concat(cargos))
            if (!string.IsNullOrEmpty(it.Mark)) occupied.Add(it.Mark);
        var taskIds = new ConcurrentBag<string>();
        var taskDetails = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var childId = string.IsNullOrEmpty(chain.ChildId) ? $"resume_{tplId[..Math.Min(tplId.Length, 12)]}" : chain.ChildId;
        var resumed = 0;
        // 断点任务已等待完成 → 跳过该步骤从下一步继续；否则（断点步骤未下发）从该步骤重跑
        var startIndex = string.IsNullOrEmpty(waitingId) ? chain.StepIndex : chain.StepIndex + 1;
        for (var i = startIndex; i < tpl.Steps.Count; i++)
        {
            var step = tpl.Steps[i];
            try
            {
                UpsertChain(tplId, i, childId, chain.ParentId ?? "", ctx);
                if (step.Kind == AutoStepKinds.PickPallet)
                {
                    var pool = step.PalletFilter switch
                    {
                        "Loaded" => loadedPallets,
                        "Any" => emptyPallets.Concat(loadedPallets).ToList(),
                        _ => emptyPallets,
                    };
                    if (pool.Count == 0) { _log.Add(childId, $"恢复链 {tpl.Name} 步骤{i + 1} 选托盘失败：{step.PalletFilter} 池为空", "#f87171"); break; }
                    var pick = pool[Random.Shared.Next(pool.Count)];
                    emptyPallets.Remove(pick); loadedPallets.Remove(pick);
                    lock (_busyLock) _busyContainers.Add(pick.Code);
                    ctx.PalletCode = pick.Code; ctx.PalletMark = pick.Mark; ctx.ContainerCode = pick.Code;
                    ctx.LastEndMark = pick.Mark;
                    _log.Add(childId, $"恢复链 {tpl.Name} 选托盘：{pick.Code} @ {pick.Mark}", "#38bdf8");
                }
                else if (step.Kind == AutoStepKinds.PickCargo)
                {
                    if (cargos.Count == 0) { _log.Add(childId, $"恢复链 {tpl.Name} 步骤{i + 1} 选货物失败：货物池为空", "#f87171"); break; }
                    var pick = cargos[Random.Shared.Next(cargos.Count)];
                    cargos.Remove(pick);
                    ctx.CargoCode = pick.Code; ctx.CargoMark = pick.Mark; ctx.ContainerCode = pick.Code;
                    ctx.LastEndMark = pick.Mark;
                    _log.Add(childId, $"恢复链 {tpl.Name} 选货物：{pick.Code} @ {pick.Mark}", "#38bdf8");
                }
                else if (step.Kind == AutoStepKinds.PickLoadedPallet)
                {
                    if (loadedPallets.Count == 0) { _log.Add(childId, $"恢复链 {tpl.Name} 步骤{i + 1} 选带货托失败：带货托池为空", "#f87171"); break; }
                    var pick = loadedPallets[Random.Shared.Next(loadedPallets.Count)];
                    loadedPallets.Remove(pick);
                    lock (_busyLock) _busyContainers.Add(pick.Code);
                    if (!string.IsNullOrEmpty(pick.CargoCode)) lock (_busyLock) _busyContainers.Add(pick.CargoCode);
                    var cargoCode = pick.CargoCode ?? pick.Code;
                    ctx.PalletCode = pick.Code; ctx.PalletMark = pick.Mark;
                    ctx.CargoCode = cargoCode; ctx.CargoMark = pick.Mark;
                    ctx.ContainerCode = cargoCode;
                    ctx.LastEndMark = pick.Mark;
                    _log.Add(childId, $"恢复链 {tpl.Name} 选带货托：{cargoCode} (托盘 {pick.Code}) @ {pick.Mark}", "#38bdf8");
                }
                else if (step.Kind == AutoStepKinds.RunTemplate)
                {
                    var tid = await RunTemplateStep(step, ctx, settings, occupied, childId, taskIds, taskDetails);
                    if (tid != null)
                    {
                        resumed++;
                        SetChainWaiting(tplId, tid);
                        ClearChainWaiting(tplId);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Add(childId, $"恢复链 {tpl.Name} 步骤{i + 1} 异常：{ex.Message}", "#f87171");
            }
        }
        RemoveChain(tplId);
        if (resumed > 0)
        {
            _log.Add($"🔁 恢复链「{tpl.Name}」续跑完成：{resumed} 个任务已从断点继续下发（沿用前置容器，无需重新选托盘）", "#4ade80");
            if (taskIds.Count > 0)
            {
                try { await Task.WhenAll(taskIds.Select(id => _stage.WaitFinishedAsync(id))); } catch { }
                _log.Add($"✓ 恢复链「{tpl.Name}」全部续跑任务已完成，日志已清理", "#4ade80");
                if (!string.IsNullOrEmpty(chain.ParentId)) _log.ClearRound(chain.ParentId!);
            }
        }
        else
        {
            _log.Add($"⚠ 恢复链「{tpl.Name}」无任务续跑（断点步骤无法执行），已放弃该链", "#fbbf24");
        }
        PersistState();
    }

    /// <summary>恢复跟踪：等待任务 FINISHED（GRCS 回调驱动），完成后释放站点锁与容器占用（无限等待，不超时）。</summary>
    private async Task ResumeTrackingAsync(string taskId)
    {
        try { await _stage.WaitFinishedAsync(taskId); }
        catch { }
        ReleaseTaskLocks(taskId);
        if (_containerByTask.TryRemove(taskId, out var cont))
            lock (_busyLock) _busyContainers.Remove(cont);
        if (_loadedPalletByTask.TryRemove(taskId, out var pallet))
            lock (_busyLock) _busyContainers.Remove(pallet);
        _log.Add($"✓ 恢复任务 {taskId} 已完成，占用与站点锁已释放", "#4ade80");
        PersistState();
    }

    public (bool ok, string reason) Start(string? tabId, List<string>? templateIds)
    {
        if (_chains.Count > 0)
        {
            _log.Add("启动被拒：执行链断点恢复中，请等待续跑完成后再启动", "#f87171");
            return (false, "执行链断点恢复中，请稍候启动");
        }
        if (!_mocks.HasTaskStageRule())
        {
            const string reason = "未配置任务阶段卡（Mock 卡片勾选「关联任务看板」），禁止启动轮询，请先在信号交互→通用 Mock 入站配置";
            _log.Add("启动失败：" + reason, "#f87171");
            return (false, reason);
        }
        var ids = (templateIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count == 0)
        {
            _log.Add("启动失败：未指定自动化模板", "#f87171");
            return (false, "未指定自动化模板");
        }
        var all = _templates.GetAll();
        var missing = ids.FirstOrDefault(id => !all.Any(t => t.Id == id));
        if (missing != null)
        {
            _log.Add($"启动失败：模板不存在 {missing}", "#f87171");
            return (false, $"模板不存在 {missing}");
        }
        if (!_gate.TryStartAuto(tabId))
        {
            _log.Add("启动被拒：移动任务循环或批量任务正在运行", "#f87171");
            return (false, "移动任务循环或批量任务正在运行");
        }
        lock (_stateLock)
        {
            _activeTemplateIds = ids;
            _activeTabId = tabId;
            _running = true;
            _status = "轮询中";
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _log.Add($"自动化模板轮询启动：{ActiveTemplateName}", "#4ade80");
        PersistState();
        _ = PollLoop(_cts.Token);
        return (true, "");
    }

    public void Stop(bool hostShutdown = false)
    {
        lock (_stateLock)
        {
            if (!_running) return;
            _running = false;
            _status = "已停止";
        }
        _gate.StopAuto();
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _log.Add("自动化模板轮询停止", "#fbbf24");
        // Host 关闭（重启/退出）时保留持久化 Running=true，重启后自动恢复轮询；用户手动停止才写 false
        if (!hostShutdown) PersistState();
    }

    /// <summary>强制结束：停止轮询、无限等待的本轮任务不再等待 FINISHED、释放所有站点锁与容器占用、清空所有自动化日志。</summary>
    public void ForceEnd()
    {
        lock (_stateLock)
        {
            _running = false;
            _status = "已强制结束";
        }
        _gate.StopAuto();
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        // 解除所有无限等待的 FINISHED 阻塞，使 WaitRoundCompletion 得以继续并自动清除
        try { _stage.ForceCompleteAll(); } catch { }
        // 释放所有站点锁与容器占用
        foreach (var kv in _lockByTask) { try { _locks.Release(kv.Value); } catch { } }
        foreach (var kv in _endLockByTask) { try { _locks.Release(kv.Value); } catch { } }
        _lockByTask.Clear();
        _endLockByTask.Clear();
        _containerByTask.Clear();
        _loadedPalletByTask.Clear();
        _chains.Clear();
        lock (_busyLock) _busyContainers.Clear();
        // 清空所有自动化轮次日志
        _log.Clear();
        _log.Add("⚠ 已强制结束当前轮次：所有任务不再等待 FINISHED，相关占用已释放", "#f87171");
        // 清除持久化运行状态：重启后不再自动恢复，从零开始
        try { _db.KvRemove(RunStateKey); } catch { }
    }

    public void SetInterval(int ms)
    {
        lock (_stateLock) _interval = Math.Clamp(ms, 200, 600_000);
    }

    /// <summary>单次执行（保留给 /api/wcs/auto/execute）：立即按模板下发一次；count 仅控制重复轮数（默认 1）。每次执行是一个独立轮次。</summary>
    public async Task ExecuteOnce(string? templateId, int count, int? intervalMs, string? tabId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return;
        if (_gate.AutoRunning) { _log.Add("单次执行被拒：轮询自动化正在运行，请先停止轮询", "#f87171"); return; }
        if (_gate.MoveRunning) { _log.Add("单次执行被拒：纯移动任务循环正在运行", "#f87171"); return; }
        var tpl = _templates.GetAll().FirstOrDefault(t => t.Id == templateId);
        if (tpl == null) return;
        var n = Math.Clamp(count, 1, 200);
        for (var i = 0; i < n; i++)
        {
            var pendingNo = _roundSeq + 1;
            var parentId = _log.BeginRound($"第 {pendingNo} 轮 · 手动执行");
            var (ids, details) = await RunTemplates(new List<AutoTemplateDto> { tpl }, parentId);
            if (ids.Count > 0) _ = WaitRoundCompletion(ids, details, parentId);
            if (intervalMs is > 0 && i < n - 1) await Task.Delay(intervalMs.Value);
        }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (ct.IsCancellationRequested) break;
            if (!_running) { try { await Task.Delay(200, ct); } catch { break; } continue; }
            var tpls = _activeTemplateIds
                .Select(id => _templates.GetAll().FirstOrDefault(t => t.Id == id))
                .Where(t => t != null)
                .ToList();
            if (tpls.Count == 0) { try { await Task.Delay(_interval, ct); } catch { break; } continue; }
            var pendingNo = _roundSeq + 1;
            var parentId = _log.BeginRound($"第 {pendingNo} 轮 · 自动循环");
            _ = RunRoundAsync(tpls!, parentId);
            try { await Task.Delay(_interval, ct); } catch { break; }
        }
    }

    /// <summary>跑完一轮（可能含等待 FINISHED 的步骤），然后等该轮所有任务完成后清除标题与日志。</summary>
    private async Task RunRoundAsync(List<AutoTemplateDto> tpls, string parentId)
    {
        try
        {
            var (ids, details) = await RunTemplates(tpls, parentId);
            if (ids.Count > 0) _ = WaitRoundCompletion(ids, details, parentId);
            else _log.ClearRound(parentId);   // 空轮次（无库存/无候选等）不留空标题，避免日志堆积
        }
        catch (Exception ex)
        {
            _log.Add(parentId, $"轮次执行异常：{ex.Message}", "#f87171");
            _log.CompleteRound(parentId);
        }
    }

    /// <summary>等待本轮所有任务 FINISHED（无限等待），完成后清除整轮日志（含所有子任务）并在系统通知中提示详细信息。</summary>
    private async Task WaitRoundCompletion(List<string> taskIds, IReadOnlyDictionary<string, string> taskDetails, string parentId)
    {
        var waitAll = Task.WhenAll(taskIds.Select(async id => { try { await _stage.WaitFinishedAsync(id); } catch { } }));
        await waitAll;
        var parent = _log.GetRounds().FirstOrDefault(r => r.RoundId == parentId);
        var title = parent?.Title ?? parentId;
        var start = parent?.StartTime ?? "";
        var end = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string detailStr;
        if (taskDetails.Count > 0)
        {
            var i = 0;
            detailStr = string.Join("；", taskDetails.Select(kv => $"[{++i}] {kv.Value} ({kv.Key})"));
        }
        else
            detailStr = string.Join("、", taskIds);
        _log.Add($"✓ {title} 全部完成 — 发起 {start} · 完成 {end} · 共 {taskIds.Count} 个任务：{detailStr} ，日志已自动清除", "#4ade80");
        _log.ClearRound(parentId);
    }

    private record InvItem(string Code, string Mark, bool IsLoaded, string? CargoCode = null, string? Station = null);

    /// <summary>按模板集合执行一轮（一次轮询下发一次选中的模板实例）。
    /// 共用同一份库存快照：每个模板各自一条执行链(ctx)，选托盘/选货物在 lock 下从共享池抽取并移除，
    /// 因此多个模板不会选到同一托盘/货物（避免并发撞车）；各模板的 RunTemplateStep 并发下发到 GRCS。
    /// 单选 = 一个模板下发一次；多选 = 同时下发多个模板（间隔到了再整体来一轮）。</summary>
    private async Task<(List<string> ids, IReadOnlyDictionary<string, string> details)> RunTemplates(List<AutoTemplateDto> tpls, string roundId)
    {
        var taskIds = new ConcurrentBag<string>();
        var taskDetails = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tpls == null || tpls.Count == 0) return (taskIds.ToList(), taskDetails);
        var pendingNo = _roundSeq + 1;
        bool renamed = false;
        // 本轮是否真正需要库存（含选托盘/选货物步骤）。纯 RunTemplate（直接入库）模板不依赖库存，不应因库存为空而跳过。
        var needsInventory = tpls.Any(t => t.Steps.Any(s => s.Kind == AutoStepKinds.PickPallet || s.Kind == AutoStepKinds.PickCargo || s.Kind == AutoStepKinds.PickLoadedPallet));
        int? seq = null;
        void MarkFirst(string? childId, string name)
        {
            if (!seq.HasValue) { seq = Interlocked.Increment(ref _roundSeq); PersistState(); }
            if (!string.IsNullOrEmpty(childId)) _log.RenameRound(childId, name);
        }
        var settings = _settings.Get();
        if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl))
        {
            var cid = _log.BeginRound("未配置 GRCS", roundId);
            _log.Add(cid, "未配置 GRCS 地址（连接设置页填写）", "#f87171");
            return (taskIds.ToList(), taskDetails);
        }
        var (emptyPallets, loadedPallets, cargos, snapOk) = await SnapshotAsync(settings, roundId);
        if (needsInventory && emptyPallets.Count == 0 && loadedPallets.Count == 0 && cargos.Count == 0)
        {
            // 无库存：不进轮次日志，只在系统通知中更新一条（时间随每轮刷新，可看到最后一次检查时间）；查询失败与真空区分提示
            var reason = snapOk
                ? "无可用库存（空托/带货托/货物均为 0），等待库存补充后自动下发"
                : "库存查询失败（GRCS 未响应），等待下轮重试";
            _log.AddOrUpdate("[无库存]", $"自动化轮询：{reason}", "#fbbf24");
            return (taskIds.ToList(), taskDetails);
        }

        // 当前被货/托盘占用的站点（任何占用都算），供「终点不能有货」选点过滤使用
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in emptyPallets.Concat(loadedPallets).Concat(cargos))
            if (!string.IsNullOrEmpty(it.Mark)) occupied.Add(it.Mark);

        var poolLock = new object();

        async Task RunOne(AutoTemplateDto tpl, int k, string childId)
        {
            var name = tpl.Name;
            var ctx = new ExecCtx();
            bool first = true;
            for (var i = 0; i < tpl.Steps.Count; i++)
            {
                var step = tpl.Steps[i];
                try
                {
                    UpsertChain(tpl.Id, i, childId, roundId, ctx); // 断点：当前步骤 + ctx 快照
                    if (first && step.Kind == AutoStepKinds.RunTemplate) MarkFirst(childId, name);
                    first = false;
                    if (step.Kind == AutoStepKinds.PickPallet)
                    {
                        InvItem? pick;
                        lock (poolLock)
                        {
                            var pool = step.PalletFilter switch
                            {
                                "Loaded" => loadedPallets,
                                "Any" => emptyPallets.Concat(loadedPallets).ToList(),
                                _ => emptyPallets,
                            };
                            if (pool.Count == 0) { _log.Add(childId, $"第 {pendingNo} 轮 模板{k + 1} 选托盘失败：{step.PalletFilter} 池为空，等待下轮下发", "#f87171"); return; }
                            pick = pool[Random.Shared.Next(pool.Count)];
                            emptyPallets.Remove(pick); loadedPallets.Remove(pick);
                            lock (_busyLock) _busyContainers.Add(pick.Code); // 占用中，跨轮排除
                            PersistState();
                        }
                        ctx.PalletCode = pick.Code; ctx.PalletMark = pick.Mark; ctx.ContainerCode = pick.Code;
                        ctx.LastEndMark = pick.Mark;
                        _log.Add(childId, $"模板{k + 1} 选托盘：{pick.Code} @ {pick.Mark}", "#38bdf8");
                        MarkFirst(childId, name);
                    }
                    else if (step.Kind == AutoStepKinds.PickCargo)
                    {
                        InvItem? pick;
                        lock (poolLock)
                        {
                            if (cargos.Count == 0) { _log.Add(childId, $"第 {pendingNo} 轮 模板{k + 1} 选货物失败：货物池为空，等待下轮下发", "#f87171"); return; }
                            pick = cargos[Random.Shared.Next(cargos.Count)];
                            cargos.Remove(pick);
                        }
                        ctx.CargoCode = pick.Code; ctx.CargoMark = pick.Mark; ctx.ContainerCode = pick.Code;
                        ctx.LastEndMark = pick.Mark;
                        _log.Add(childId, $"模板{k + 1} 选货物：{pick.Code} @ {pick.Mark}", "#38bdf8");
                        MarkFirst(childId, name);
                    }
                    else if (step.Kind == AutoStepKinds.PickLoadedPallet)
                    {
                        InvItem? pick;
                        lock (poolLock)
                        {
                            if (loadedPallets.Count == 0) { _log.Add(childId, $"第 {pendingNo} 轮 模板{k + 1} 选带货托失败：带货托池为空，等待下轮下发", "#f87171"); return; }
                            pick = loadedPallets[Random.Shared.Next(loadedPallets.Count)];
                            loadedPallets.Remove(pick);
                            lock (_busyLock) _busyContainers.Add(pick.Code); // 占用中，跨轮排除（托盘号）
                            if (!string.IsNullOrEmpty(pick.CargoCode)) lock (_busyLock) _busyContainers.Add(pick.CargoCode);
                            PersistState();
                        }
                        var cargoCode = pick.CargoCode ?? pick.Code;
                        ctx.PalletCode = pick.Code; ctx.PalletMark = pick.Mark;
                        ctx.CargoCode = cargoCode; ctx.CargoMark = pick.Mark;
                        ctx.ContainerCode = cargoCode; // 带货托取货物号而非托盘号
                        ctx.LastEndMark = pick.Mark;
                        _log.Add(childId, $"模板{k + 1} 选带货托：{cargoCode} (托盘 {pick.Code}) @ {pick.Mark}", "#38bdf8");
                        MarkFirst(childId, name);
                    }
                    else if (step.Kind == AutoStepKinds.RunTemplate)
                    {
                        var tid = await RunTemplateStep(step, ctx, settings, occupied, childId, taskIds, taskDetails);
                        if (tid != null)
                        {
                            MarkFirst(childId, name);
                            SetChainWaiting(tpl.Id, tid);     // 断点：步骤任务已下发，等待 FINISHED
                            ClearChainWaiting(tpl.Id);        // RunTemplateStep 返回时已等完（WaitForFinish/终点模块）
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Add(childId, $"模板{k + 1} 步骤执行异常：{ex.Message}", "#f87171");
                }
            }
            RemoveChain(tpl.Id); // 链完成，移除断点
        }

        var tasks = new List<Task>(tpls.Count);
        for (int k = 0; k < tpls.Count; k++)
        {
            var idx = k;
            var cid = _log.BeginRound(tpls[idx].Name, roundId);
            tasks.Add(Task.Run(() => RunOne(tpls[idx], idx, cid)));
        }
        await Task.WhenAll(tasks);
        return (taskIds.ToList(), taskDetails);
    }

    /// <summary>保存前校验：每个 RunTemplate 步骤引用的任务模板，
    /// ① 终点类型必须在选点范围内存在对应站点；
    /// ② 起点类型（无论是否取自前置终点）必须在选点范围内存在对应站点；
    /// ③ 若「起点取自前置终点」且其起点类型已配置，需与前一步的终点类型兼容（有交集），否则链路衔接会失败；
    /// ④ 结构校验：「使用前置挑选的容器」前面必须有选托盘/选货物步骤；「起点取自前置终点」不能作为第一步。
    /// 校验失败返回错误列表，保存接口据此拒绝保存。</summary>
    public List<string> ValidateTemplates(List<AutoTemplateDto> items)
    {
        var errors = new List<string>();
        var range = _range.Get();
        var rangeSet = (range.Enabled && range.Marks.Count > 0)
            ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase)
            : null;
        var allStations = _map.GetStations();
        var stations = rangeSet == null
            ? allStations
            : allStations.Where(s => rangeSet.Contains(s.Mark)).ToList();

        bool HasType(int bits) => bits != 0 && stations.Any(s => (s.StationType & bits) != 0);

        foreach (var tpl in items ?? [])
        {
            TaskTemplateDto? prevTt = null;
            var seenPick = false;
            for (var i = 0; i < tpl.Steps.Count; i++)
            {
                var step = tpl.Steps[i];
                if (step.Kind == AutoStepKinds.PickPallet || step.Kind == AutoStepKinds.PickCargo || step.Kind == AutoStepKinds.PickLoadedPallet)
                {
                    // 选托盘/选货物：提供前置容器与“终点”（库存所在站，类型动态）；记录已出现，供后续步骤校验
                    seenPick = true;
                    prevTt = null;
                    continue;
                }
                if (step.Kind != AutoStepKinds.RunTemplate)
                {
                    prevTt = null;
                    continue;
                }
                var tt = _taskTemplates.GetAll().FirstOrDefault(t => string.Equals(t.Value, step.TemplateValue, StringComparison.OrdinalIgnoreCase));
                if (tt == null)
                {
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}：引用的任务模板不存在（{step.TemplateValue}）");
                    prevTt = null;
                    continue;
                }

                // 结构校验：前置步骤存在性
                if (step.UsePickedContainer && !seenPick)
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：勾选了「使用前置挑选的容器」，但其前面没有选托盘/选货物步骤");
                if (step.UsePickedStart && i == 0)
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：勾选了「起点取自前置终点」，但不能作为第一步（前面没有可衔接的步骤）");

                // 终点：始终校验范围内有对应类型站点
                var eb = tt.End?.StationTypeBits ?? 0;
                if (eb != 0 && !HasType(eb))
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：终点类型「{BitsName(eb)}」在选点范围内无匹配站点");

                // 起点：无论是否取自前置终点，范围内都必须存在对应类型站点（链式起点也落在范围内）
                var sb = tt.Start?.StationTypeBits ?? 0;
                if (sb != 0 && !HasType(sb))
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：起点类型「{BitsName(sb)}」在选点范围内无匹配站点");

                // 起点取自前置终点：与其前置步骤的终点类型比较（类型需有交集，否则衔接必失败）
                if (step.UsePickedStart && sb != 0 && prevTt != null)
                {
                    var prevEnd = prevTt.End?.StationTypeBits ?? 0;
                    if (prevEnd != 0 && (prevEnd & sb) == 0)
                        errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）起点类型「{BitsName(sb)}」与步骤{i}（{prevTt.Label}）终点类型「{BitsName(prevEnd)}」不匹配（起点取自前置终点）");
                }

                prevTt = tt;
            }
        }
        return errors;
    }

    /// <summary>执行一步任务模板：选点、组装任务、经模块下发。返回下发的任务号（失败返回 null）。</summary>
    private async Task<string?> RunTemplateStep(AutoStepDto step, ExecCtx ctx, WcsSettingsDto settings, HashSet<string> occupied, string roundId, ConcurrentBag<string> taskIds, ConcurrentDictionary<string, string> taskDetails)
    {
        var tpl = _taskTemplates.GetAll().FirstOrDefault(t => string.Equals(t.Value, step.TemplateValue, StringComparison.OrdinalIgnoreCase));
        if (tpl == null) { _log.Add(roundId, $"任务模板缺失：{step.TemplateValue}", "#f87171"); return null; }
        // 选点范围约束：库存挑选已按范围过滤；起点/终点选点也必须在范围内。
        var range = _range.Get();
        var rangeSet = (range.Enabled && range.Marks.Count > 0)
            ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase)
            : null;
        var allStations = _map.GetStations();
        var stations = rangeSet == null
            ? allStations
            : allStations.Where(s => rangeSet.Contains(s.Mark)).ToList();

        var usePickedContainer = step.UsePickedContainer;
        var usePickedStart = step.UsePickedStart;
        var hasPick = !string.IsNullOrEmpty(ctx.ContainerCode);

        string? container;
        string? startMark;

        // 容器：取自前置挑选 or 按模板生成
        if (usePickedContainer)
        {
            if (!hasPick) { _log.Add(roundId, $"模板[{tpl.Label}] 容器使用前置挑选，但无可用托盘/货物，跳过", "#f87171"); return null; }
            container = ctx.ContainerCode;
        }
        else if (tpl.NeedsContainer)
        {
            container = GenerateContainerCode(tpl.ContainerPrefix);
            ctx.ContainerCode = container; // 生成的容器作为后续「使用前置容器」的来源
            _log.Add(roundId, $"模板[{tpl.Label}] 自动生成容器：{container}", "#38bdf8");
        }
        else
        {
            container = "";
        }

        // 起点：取自前置步骤终点（链路衔接）or 按模板起点类型在范围内选点
        if (usePickedStart)
        {
            if (string.IsNullOrEmpty(ctx.LastEndMark)) { _log.Add(roundId, $"模板[{tpl.Label}] 起点使用前置终点，但无前置终点可用，跳过", "#f87171"); return null; }
            startMark = ctx.LastEndMark;
        }
        else
        {
            var startBits = tpl.Start?.StationTypeBits ?? 0;
            if (startBits == 0)
            {
                _log.Add(roundId, $"模板[{tpl.Label}] 未配置起点站点类型，无法自动选点，跳过", "#f87171");
                return null;
            }
            // 排除已被其它任务锁定的起点，避免重复选点（真正去重）
            var lockedStarts = _locks.GetLocked(_stage);
            var startPool = stations.Where(x => (x.StationType & startBits) != 0 && !lockedStarts.Contains(x.Mark)).ToList();
            var s = startPool.Count == 0 ? null : startPool[Random.Shared.Next(startPool.Count)];
            if (s == null) { _log.Add(roundId, $"模板[{tpl.Label}] 起点范围内无可匹配站点（需 {BitsName(startBits)}），跳过", "#f87171"); return null; }
            startMark = s.Mark;
        }

        // 起点站点类型约束校验（usePicked 时确保库存站符合模板起点类型）
        var startBitsChk = tpl.Start?.StationTypeBits ?? 0;
        if (startBitsChk != 0 && !string.IsNullOrEmpty(startMark))
        {
            var st = stations.FirstOrDefault(s => string.Equals(s.Mark, startMark, StringComparison.OrdinalIgnoreCase));
            if (st == null || (st.StationType & startBitsChk) == 0)
            {
                _log.Add(roundId, $"模板[{tpl.Label}] 起点站点类型不匹配（{startMark} 需 {BitsName(startBitsChk)}），跳过", "#f87171");
                return null;
            }
        }

        var startWcs = WcsOf(startMark, stations) ?? startMark ?? "";
        var dest = ChooseDestination(tpl, stations, occupied);
        if (dest == null)
        {
            var endBits = tpl.End?.StationTypeBits ?? 0;
            if (endBits != 0 && stations.Any(s => (s.StationType & endBits) != 0)
                && !stations.Any(s => (s.StationType & endBits) != 0 && !occupied.Contains(s.Mark)))
                _log.Add(roundId, $"模板[{tpl.Label}] 终点范围内匹配站点均被占用（需 {BitsName(endBits)}），跳过", "#f87171");
            else
                _log.Add(roundId, $"模板[{tpl.Label}] 终点范围内无可匹配站点（需 {BitsName(endBits)}），跳过", "#f87171");
            return null;
        }
        var destWcs = dest.ToWcsCode();
        var taskId = "Auto_" + Guid.NewGuid().ToString("N")[..12];
        var mctx = new ModuleRunService.ModuleCtx
        {
            Start = startWcs,
            End = destWcs,
            Container = container,
            Warehouse = settings.SceneName,
            TaskType = tpl.Value,
            TaskId = taskId,
        };

        var group = new WcsTaskGroup
        {
            GroupId = "G_" + Guid.NewGuid().ToString("N")[..10],
            MsgTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            PriorityCode = 5,
            Warehouse = settings.SceneName,
            Tasks = [new WcsTaskItem
            {
                TaskId = taskId,
                TaskType = tpl.Value,
                ContainerCode = container,
                StationCode = [startWcs, destWcs],
                AreaCode = [],
            }],
        };

        if (!string.IsNullOrEmpty(startMark)) { _locks.Acquire(startMark, taskId); _lockByTask[taskId] = startMark; }
        if (!string.IsNullOrEmpty(dest?.Mark)) { _locks.Acquire(dest.Mark, taskId); _endLockByTask[taskId] = dest.Mark; }

        // 统一经 ModuleRunService：起点模块(下发前) → 下发 → 起点之后模块(下发成功后)
        var (ok, code, json) = await _modules.SendTaskWithModulesAsync(group, roundId);
        if (ok)
        {
            Interlocked.Increment(ref _executed);
            taskIds.Add(taskId);
            taskDetails[taskId] = $"{tpl.Label} [{container}] {startWcs}->{destWcs}";
            _containerByTask[taskId] = container; // 记录任务→容器，FINISHED 后解除占用
            if (!string.IsNullOrEmpty(ctx.PalletCode) && !string.Equals(ctx.PalletCode, container, StringComparison.OrdinalIgnoreCase))
                _loadedPalletByTask[taskId] = ctx.PalletCode; // 带货托额外记录托盘号，FINISHED 后一并释放
            _log.Add(roundId, $"下发 {taskId} ({tpl.Label}) [{container}] {startWcs}→{destWcs}", "#4ade80");
            PersistState(); // 任务已下发：持久化任务→容器/托盘/站点锁，重启后可恢复跟踪
            // 本步终点作为后续步骤（起点取自前置终点）的前置终点
            ctx.LastEndMark = dest.Mark;
            // FINISHED 后释放起点锁
            _ = ReleaseOnFinishAsync(taskId);
            // 终点模块由自动化任务自己跑（避免 FinishedModuleWatcher 重复执行 Auto_ 任务）。
            // 有终点模块 → 强制等待 FINISHED 后跑终点模块（等其 success）；无终点模块 → 按 WaitForFinish 等 FINISHED。
            var endIds = tpl.End?.AfterModules ?? [];
            try
            {
                if (endIds.Count > 0)
                {
                    await _stage.WaitFinishedAsync(taskId);
                    await _modules.RunEndModulesAsync(taskId, mctx, roundId);
                }
                else if (step.WaitForFinish)
                {
                    await _stage.WaitFinishedAsync(taskId);
                }
            }
            catch (Exception ex)
            {
                _log.Add(roundId, $"终点阶段异常 {taskId}：{ex.Message}", "#f87171");
            }
            return taskId;
        }
        else
        {
            _log.Add(roundId, $"下发失败 {taskId}：HTTP {code} {json[..Math.Min(json.Length, 200)]}", "#f87171");
            ReleaseTaskLocks(taskId);
            // 下发失败立即释放本次占用的容器（带货托需同时释放托盘与货物）
            lock (_busyLock) { _busyContainers.Remove(container); if (!string.IsNullOrEmpty(ctx.PalletCode)) _busyContainers.Remove(ctx.PalletCode); }
            PersistState();
        }
        return null;
    }

    private async Task ReleaseOnFinishAsync(string taskId)
    {
        try { await _stage.WaitFinishedAsync(taskId); }
        catch { }
        ReleaseTaskLocks(taskId);
        if (_containerByTask.TryRemove(taskId, out var cont))
            lock (_busyLock) _busyContainers.Remove(cont);
        if (_loadedPalletByTask.TryRemove(taskId, out var pallet))
            lock (_busyLock) _busyContainers.Remove(pallet);
        PersistState();
    }

    /// <summary>释放某个任务持有的全部站点锁（起点 + 终点）。</summary>
    private void ReleaseTaskLocks(string taskId)
    {
        if (_lockByTask.TryRemove(taskId, out var mk)) _locks.Release(mk);
        if (_endLockByTask.TryRemove(taskId, out var emk)) _locks.Release(emk);
    }

    /// <summary>查询 GRCS 库存并按「以前的逻辑」分类统计：纯空托 / 带货托 / 纯货物 / 锁定中。
    /// 纯货物 = 编码含 Cargo 且无同站点托盘；托盘 = 编码含 Container；带货托 = 同当前站点有关联货物；锁定中 = 被任务锁定的容器数量（_busyContainers，含已出库但任务未释放的）。</summary>
    public async Task<(int Empty, int Loaded, int Cargo, int Locked)> GetInventorySummaryAsync()
    {
        var settings = _settings.Get();
        if (settings == null) return (0, 0, 0, 0);
        var (ok, _, json) = await _grcs.QueryCargoInventoryAsync(settings.GrcsBaseUrl, settings.SceneName);
        if (!ok) return (0, 0, 0, 0);
        var inv = JsonSerializer.Deserialize<CargoQueryResult>(json, JsonOpts);
        var range = _range.Get();
        var rangeSet = (range.Enabled && range.Marks.Count > 0)
            ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase) : null;
        // 被任务锁定的容器数量（含已出库但任务尚未释放的）
        int locked; lock (_busyLock) locked = _busyContainers.Count;
        var records = inv?.Data?.Records ?? [];
        // 仅统计「储位」内的库存（与选池一致，排除分拣/接驳位流转中的货）
        var stations = _map.GetStations();
        var storageMarks = new HashSet<string>(stations.Where(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0).Select(s => s.Mark), StringComparer.OrdinalIgnoreCase);
        records = records.Where(c => storageMarks.Contains(c.CurrentStationCode ?? "")).ToList();
        // 第一遍：收集 Container / Cargo 的当前站点（同站点关联判定，保证带货托与纯货物互斥）
        var containerStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cargoStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in records)
        {
            if (rangeSet != null && !rangeSet.Contains(c.CurrentStationCode ?? "")) continue;
            var code = c.Code ?? "";
            if (code.Contains("Cargo", StringComparison.OrdinalIgnoreCase)) cargoStations.Add(c.CurrentStationCode ?? "");
            else if (code.Contains("Container", StringComparison.OrdinalIgnoreCase)) containerStations.Add(c.CurrentStationCode ?? "");
        }
        int empty = 0, loaded = 0, cargo = 0;
        foreach (var c in records)
        {
            if (rangeSet != null && !rangeSet.Contains(c.CurrentStationCode ?? "")) continue;
            var code = c.Code ?? "";
            if (code.Contains("Cargo", StringComparison.OrdinalIgnoreCase))
            {
                // 纯货物 = 所在站点没有同站点托盘的独立货物（在托盘上的货算带货托一部分，不重复计）
                if (!containerStations.Contains(c.CurrentStationCode ?? "")) cargo++;
            }
            else if (code.Contains("Container", StringComparison.OrdinalIgnoreCase))
            {
                // 被任务锁定的容器不计入空托/带货托（已算在「锁定中」）
                bool busy; lock (_busyLock) busy = _busyContainers.Contains(code);
                if (busy) continue;
                // 带货托 = 同当前站点有关联货物；否则空托
                if (cargoStations.Contains(c.CurrentStationCode ?? "")) loaded++; else empty++;
            }
        }
        return (empty, loaded, cargo, locked);
    }

    private async Task<(List<InvItem> Empty, List<InvItem> Loaded, List<InvItem> Cargo, bool Ok)> SnapshotAsync(WcsSettingsDto settings, string roundId)
    {
        var empty = new List<InvItem>();
        var loaded = new List<InvItem>();
        var cargo = new List<InvItem>();
        try
        {
            var (ok, _, json) = await _grcs.QueryCargoInventoryAsync(settings.GrcsBaseUrl, settings.SceneName);
            if (!ok) { _log.Add(roundId, $"库存查询失败（HTTP 非成功）", "#f87171"); return (empty, loaded, cargo, false); }
            var inv = JsonSerializer.Deserialize<CargoQueryResult>(json, JsonOpts);
            var range = _range.Get();
            var rangeSet = (range.Enabled && range.Marks.Count > 0)
                ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase)
                : null;
            var all = (inv?.Data?.Records ?? []).Where(x => rangeSet == null || rangeSet.Contains(x.CurrentStationCode ?? "")).ToList();
            // 仅从「储位」选：排除分拣台 / 接驳位等流转中位置的货物与托盘，避免选中正在分拣的货
            var stations = _map.GetStations();
            var storageMarks = new HashSet<string>(stations.Where(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0).Select(s => s.Mark), StringComparer.OrdinalIgnoreCase);
            // 第一遍：收集 Container / Cargo 的当前站点（同站点关联，保证带货托与纯货物互斥）并建立站点→货物号映射（带货托取货物号用）
            var containerStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cargoStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cargoByStation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in all)
            {
                if ((c.Code ?? "").Contains("Cargo", StringComparison.OrdinalIgnoreCase))
                {
                    var st = c.CurrentStationCode ?? "";
                    cargoStations.Add(st);
                    if (!string.IsNullOrEmpty(st) && !string.IsNullOrEmpty(c.Code) && !cargoByStation.ContainsKey(st))
                        cargoByStation[st] = c.Code!;
                }
                else if ((c.Code ?? "").Contains("Container", StringComparison.OrdinalIgnoreCase)) containerStations.Add(c.CurrentStationCode ?? "");
            }
            // 第二遍：构建候选池（带货托 = 同当前站点有关联货物；纯货物 = 无同站点托盘的独立货物）
            foreach (var c in all)
            {
                var code = c.Code ?? "";
                if (_busyContainers.Contains(code)) continue; // 跨轮排除已占用容器，避免下一轮重复选中
                if (!storageMarks.Contains(c.CurrentStationCode ?? "")) continue; // 仅从储位选，排除非储位（分拣/接驳）位置
                if (code.Contains("Cargo", StringComparison.OrdinalIgnoreCase))
                {
                    // 托盘上的货算带货托一部分，不单独进入货物池
                    if (!containerStations.Contains(c.CurrentStationCode ?? ""))
                        cargo.Add(new InvItem(code, c.HomeStationMark ?? "", false, null, c.CurrentStationCode));
                }
                else if (code.Contains("Container", StringComparison.OrdinalIgnoreCase))
                {
                    var st = c.CurrentStationCode ?? "";
                    var isLoaded = !string.IsNullOrEmpty(st) && cargoStations.Contains(st);
                    var cargoCode = isLoaded && cargoByStation.TryGetValue(st, out var cc) ? cc : null;
                    (isLoaded ? loaded : empty).Add(new InvItem(code, c.HomeStationMark ?? "", isLoaded, cargoCode, st));
                }
            }
        }
        catch (Exception ex) { _log.Add(roundId, $"库存查询异常：{ex.Message}", "#f87171"); return (empty, loaded, cargo, false); }
        return (empty, loaded, cargo, true);
    }

    private static string? WcsOf(string? mark, List<MapStationLite> stations)
    {
        if (string.IsNullOrEmpty(mark)) return null;
        var st = stations.FirstOrDefault(s => string.Equals(s.Mark, mark, StringComparison.OrdinalIgnoreCase));
        return st?.ToWcsCode() ?? mark;
    }

    /// <summary>
    /// 选终点：优先用模板 End.StationTypeBits 指定的站点类型（唯一权威来源）；
    /// 未配置类型约束时回退到旧的「按 Value/Category/Label 关键词猜测」逻辑。
    /// 配置了类型却无匹配站点时返回 null（调用方记录错误并跳过）。
    /// </summary>
    private static MapStationLite? ChooseDestination(TaskTemplateDto tpl, List<MapStationLite> stations, HashSet<string> occupied)
    {
        // 范围内「类型匹配且未被占用」的站点里随机选一个（不再固定取第一个）
        MapStationLite? RandomPick(Func<MapStationLite, bool> pred)
        {
            var pool = stations.Where(pred).ToList();
            return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
        }
        var bits = tpl.End?.StationTypeBits ?? 0;
        if (bits != 0)
            return RandomPick(s => (s.StationType & bits) != 0 && !occupied.Contains(s.Mark));

        // ── 未配置终点类型时的旧关键词兜底（兼容历史模板）──
        var v = $"{tpl.Value} {tpl.Category} {tpl.Label}".ToLowerInvariant();
        if (v.Contains("sort") || v.Contains("分拣"))
            return RandomPick(s => (s.StationType & MapStationTypeBits.PickingStation) != 0 && !occupied.Contains(s.Mark))
                ?? RandomPick(s => (s.StationType & MapStationTypeBits.TransferPoint) != 0 && !occupied.Contains(s.Mark))
                ?? RandomPick(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0 && !occupied.Contains(s.Mark));
        if (v.Contains("inbound") || v.Contains("入库"))
            return RandomPick(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0 && !occupied.Contains(s.Mark))
                ?? RandomPick(s => (s.StationType & MapStationTypeBits.TransferPoint) != 0 && !occupied.Contains(s.Mark));
        return RandomPick(s => (s.StationType & MapStationTypeBits.TransferPoint) != 0 && !occupied.Contains(s.Mark))
            ?? RandomPick(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0 && !occupied.Contains(s.Mark))
            ?? RandomPick(s => (s.StationType & MapStationTypeBits.PickingStation) != 0 && !occupied.Contains(s.Mark));
    }

    /// <summary>按前缀生成容器号（前缀留空默认 Container），用于不使用前置挑选的自动化步骤（如入库段2）。</summary>
    private static string GenerateContainerCode(string prefix)
    {
        var p = string.IsNullOrWhiteSpace(prefix) ? "Container" : prefix.Trim();
        return p + DateTime.Now.ToString("HHmmssfff") + Random.Shared.Next(10, 99);
    }

    /// <summary>把站点类型位解码为中文名（如 12 → "储位+接驳位"），用于日志展示。</summary>
    private static string BitsName(int bits)
    {
        if (bits == 0) return "不限";
        var names = new List<string>();
        if ((bits & MapStationTypeBits.NormalRoad) != 0) names.Add("普通路");
        if ((bits & MapStationTypeBits.HighWay) != 0) names.Add("高速路");
        if ((bits & MapStationTypeBits.StorageLocation) != 0) names.Add("储位");
        if ((bits & MapStationTypeBits.TransferPoint) != 0) names.Add("接驳位");
        if ((bits & MapStationTypeBits.Parking) != 0) names.Add("停车");
        if ((bits & MapStationTypeBits.Charging) != 0) names.Add("充电");
        if ((bits & MapStationTypeBits.PickingStation) != 0) names.Add("分拣台");
        if ((bits & MapStationTypeBits.PeopleStation) != 0) names.Add("人工台");
        if ((bits & MapStationTypeBits.Elevator) != 0) names.Add("电梯");
        if ((bits & MapStationTypeBits.Other) != 0) names.Add("其他");
        return names.Count == 0 ? $"未知({bits})" : string.Join("+", names);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private class ExecCtx
    {
        public string? PalletCode;
        public string? PalletMark;
        public string? CargoCode;
        public string? CargoMark;
        // 最近一步确定的容器（选托盘/选货物/自动生成容器），供后续「使用前置容器」取用。
        public string? ContainerCode;
        // 上一步的终点（选托盘/选货物时=库存所在站；RunTemplate 成功后=该步终点），供后续步骤「起点取自前置终点」使用。
        public string? LastEndMark;
    }
}

public class AutoTemplateStatusDto
{
    public bool Running { get; set; }
    public int Interval { get; set; }
    public string? ActiveTemplateId { get; set; }
    public string? ActiveTemplateName { get; set; }
    public string? ActiveTabId { get; set; }
    public int Executed { get; set; }
    public string Status { get; set; } = "";
    public bool GateAuto { get; set; }
    public bool AnyRunning { get; set; }
}
