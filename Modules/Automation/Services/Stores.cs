using System.Text.Json;
using GrcsBackend.Modules.Automation.Models;
using GrcsBackend.Modules.Wcs.Services;

namespace GrcsBackend.Modules.Automation.Services;

/// <summary>站点池缓存（地图上传/GRCS 拉取后持久化，重启不丢）。Singleton。</summary>
public class MapStoreService
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private readonly AutomationDb _db;
    private readonly object _lock = new();
    private List<MapStationLite> _stations = [];
    private string _savedAt = "";
    private int _pathsCount;

    public MapStoreService(AutomationDb db)
    {
        _db = db;
        try
        {
            var json = _db.KvGet("map_stations");
            if (!string.IsNullOrEmpty(json))
            {
                var dto = JsonSerializer.Deserialize<MapUploadDto>(json, Opts);
                if (dto != null) { _stations = dto.Stations; _savedAt = dto.SavedAt; _pathsCount = dto.PathsCount; }
            }
        }
        catch { }
    }

    public void Save(MapUploadDto dto)
    {
        lock (_lock)
        {
            _stations = dto.Stations ?? [];
            _savedAt = dto.SavedAt;
            _pathsCount = dto.PathsCount;
            _db.KvSet("map_stations", JsonSerializer.Serialize(dto));
        }
    }

    public List<MapStationLite> GetStations() { lock (_lock) { return _stations.ToList(); } }

    public object Snapshot() { lock (_lock) { return new { savedAt = _savedAt, pathsCount = _pathsCount, stations = _stations }; } }
}

/// <summary>选点范围配置（内存 + SQLite 持久化）。Singleton。</summary>
public class RangeConfigService
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private readonly AutomationDb _db;
    private readonly object _lock = new();
    private RangeConfigDto _range = new();

    public RangeConfigService(AutomationDb db)
    {
        _db = db;
        try
        {
            var json = _db.KvGet("auto_range");
            if (!string.IsNullOrEmpty(json))
                _range = JsonSerializer.Deserialize<RangeConfigDto>(json, Opts) ?? new RangeConfigDto();
        }
        catch { }
    }

    public RangeConfigDto Get() { lock (_lock) { return JsonSerializer.Deserialize<RangeConfigDto>(JsonSerializer.Serialize(_range))!; } }

    public void Set(RangeConfigDto range)
    {
        lock (_lock)
        {
            _range = range;
            _db.KvSet("auto_range", JsonSerializer.Serialize(_range));
        }
    }
}

/// <summary>运行配置（GRCS 地址/场景名；前端「连接设置」PUT 到这里）。Singleton。</summary>
public class WcsSettingsService
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private readonly AutomationDb _db;
    private readonly object _lock = new();
    private WcsSettingsDto _settings = new();

    public WcsSettingsService(AutomationDb db)
    {
        _db = db;
        try
        {
            var json = _db.KvGet("wcs_settings");
            if (!string.IsNullOrEmpty(json))
                _settings = JsonSerializer.Deserialize<WcsSettingsDto>(json, Opts) ?? new WcsSettingsDto();
        }
        catch { }
    }

    public WcsSettingsDto Get() { lock (_lock) { return new WcsSettingsDto { GrcsBaseUrl = _settings.GrcsBaseUrl, SceneName = _settings.SceneName }; } }

    public void Set(WcsSettingsDto s)
    {
        lock (_lock)
        {
            _settings.GrcsBaseUrl = string.IsNullOrWhiteSpace(s.GrcsBaseUrl) ? _settings.GrcsBaseUrl : s.GrcsBaseUrl.Trim();
            _settings.SceneName = s.SceneName ?? "";
            _db.KvSet("wcs_settings", JsonSerializer.Serialize(_settings));
        }
    }
}

/// <summary>段1 任务 → 货物码映射（入库段2 与信号放行共用同一码）。Singleton。</summary>
public class CargoCodeStore
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private readonly AutomationDb _db;
    private readonly object _lock = new();
    private Dictionary<string, string> _map = [];

    public CargoCodeStore(AutomationDb db)
    {
        _db = db;
        try
        {
            var json = _db.KvGet("cargo_codes");
            if (!string.IsNullOrEmpty(json))
                _map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Opts) ?? [];
        }
        catch { }
    }

    public string Ensure(string seg1TaskId)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(seg1TaskId, out var existing)) return existing;
            var code = "SimCargo_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x").ToUpper();
            _map[seg1TaskId] = code;
            _db.KvSet("cargo_codes", JsonSerializer.Serialize(_map));
            return code;
        }
    }
}

/// <summary>站点锁（纯内存 Singleton）：流程终点任务 FINISHED 后由 TaskStageService 事件即时释放。</summary>
public class StationLockStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, StationLockEntry> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前仍被锁定的站点（顺带按已完成任务惰性清理）。</summary>
    public HashSet<string> GetLocked(ITaskStageService stages)
    {
        lock (_lock)
        {
            if (_locks.Count == 0) return [];
            var finished = stages.FinishedTaskIds;
            if (finished.Count > 0)
            {
                foreach (var st in _locks.Where(kv => finished.Contains(kv.Value.TaskId)).Select(kv => kv.Key).ToList())
                    _locks.Remove(st);
            }
            return _locks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Acquire(string station, string flowEndTaskId)
    {
        if (string.IsNullOrEmpty(station)) return;
        lock (_lock)
        {
            if (_locks.ContainsKey(station)) return;
            _locks[station] = new StationLockEntry { TaskId = flowEndTaskId, Time = DateTime.Now.ToString("O") };
        }
    }
}

/// <summary>任务台账（SQLite，上限 2000 条）。Singleton。</summary>
public class LedgerStore
{
    private readonly AutomationDb _db;

    public LedgerStore(AutomationDb db) => _db = db;

    public Task AppendAsync(List<TaskLedgerEntry> entries)
    {
        _db.LedgerAppend(entries);
        return Task.CompletedTask;
    }

    public List<TaskLedgerEntry> Get(int limit = 500) => _db.LedgerGet(limit);

    public void Clear() => _db.LedgerClear();
}
