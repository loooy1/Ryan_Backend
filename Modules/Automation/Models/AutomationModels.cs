using System.Text.Json.Serialization;

namespace GrcsBackend.Modules.Automation.Models;

/// <summary>精简站点信息（与前端 MapStationLite 同构，从地图上传/GRCS 拉取后缓存）。</summary>
public class MapStationLite
{
    public string Mark { get; set; } = "";
    public int StationType { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int Floor { get; set; }
    public bool StaEnable { get; set; }
    public List<string> CargoAreas { get; set; } = [];
    public int SupportLoadState { get; set; }
    public bool AllowTurn { get; set; }
    public bool AllowAvoid { get; set; }
    public bool AllowStop { get; set; }

    /// <summary>剥掉 _0/_1 后缀，返回裸 Mark 编码（库存站点码带后缀，匹配地图键前先归一化）。</summary>
    public string ToMark()
    {
        var mark = Mark;
        if (mark.Length > 2 && (mark[^2..] is "_0" or "_1"))
            return mark[..^2];
        return mark;
    }

    /// <summary>下发编码：储位 → mark_1（放货层），接驳位/分拣台 → mark_0（车辆停靠层）。</summary>
    public string ToWcsCode()
    {
        const int StorageLocation = 4, TransferPoint = 8, PickingStation = 64, PeopleStation = 128;
        if ((StationType & StorageLocation) != 0) return Mark + "_1";
        if ((StationType & (TransferPoint | PickingStation | PeopleStation)) != 0) return Mark + "_0";
        return Mark;
    }
}

/// <summary>站点类型位标志（与前端 MapStationTypeBits 一致）。</summary>
public static class MapStationTypeBits
{
    public const int NormalRoad = 1;
    public const int HighWay = 2;
    public const int StorageLocation = 4;
    public const int TransferPoint = 8;
    public const int Parking = 16;
    public const int Charging = 32;
    public const int PickingStation = 64;
    public const int PeopleStation = 128;
    public const int Elevator = 256;
    public const int Other = 512;
}

/// <summary>选点范围配置（与前端 AutoRangeConfig 同构；范围开启后只从限定池抽点）。</summary>
public class RangeConfigDto
{
    public bool Enabled { get; set; }
    public int TypeFilter { get; set; }
    public int FloorFilter { get; set; }
    public List<string> Marks { get; set; } = [];

    /// <summary>按范围限制过滤候选站点池（类型位 + 楼层 + Mark 白名单，AND 关系）。</summary>
    public List<MapStationLite> ApplyTo(IEnumerable<MapStationLite> stations)
    {
        if (!Enabled) return stations.ToList();
        IEnumerable<MapStationLite> pool = stations;
        if (TypeFilter != 0) pool = pool.Where(s => (s.StationType & TypeFilter) != 0);
        if (FloorFilter != 0) pool = pool.Where(s => s.Floor == FloorFilter);
        if (Marks.Count > 0)
        {
            var marks = Marks.Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            pool = pool.Where(s => marks.Contains(s.Mark));
        }
        return pool.ToList();
    }

    public static List<string> ParseMarks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Split([',', '，', ';', '；', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>运行配置（GRCS 地址/场景名，前端「地图信息」页保存 PUT 到后端，代理与自动化服务读取）。</summary>
public class WcsSettingsDto
{
    public string GrcsBaseUrl { get; set; } = "http://localhost:8224";
    public string SceneName { get; set; } = "Show";
}

/// <summary>自动化运行状态（GET /api/wcs/auto/status 快照）。</summary>
public class AutoStatusDto
{
    public bool Running { get; set; }
    public int Interval { get; set; }
    public int FlowMode { get; set; }
    public int Dispatched { get; set; }
    public string Status { get; set; } = "";
    public InventoryCountsDto Inventory { get; set; } = new();
}

public class InventoryCountsDto
{
    public int EmptyPallets { get; set; }
    public int LoadedPallets { get; set; }
    public int Cargos { get; set; }
    public int PairedCargos { get; set; }
}

/// <summary>日志条目（自动化/批量执行共用，带自增 Id 供前端 sinceId 增量拉取）。</summary>
public class LogEntryDto
{
    public long Id { get; set; }
    public string Time { get; set; } = "";
    public string Message { get; set; } = "";
    public string Color { get; set; } = "#94a3b8";
}

/// <summary>批量容器任务执行请求（POST /api/wcs/auto/container/execute）。</summary>
public class ContainerExecuteRequest
{
    public int Flow { get; set; } = 1;   // 1=空托盘入库 2=带货托盘出库 3=带货托盘分拣
    public int Count { get; set; } = 3;
    public int Interval { get; set; } = 3;
}

/// <summary>任务台账条目（与前端 TaskLedgerEntry 同构；ContainerCode 恒为托盘号、CargoCode 恒为货物号）。</summary>
public class TaskLedgerEntry
{
    public string TaskId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public string CargoCode { get; set; } = "";
    public List<string> StationCode { get; set; } = [];
    public string Warehouse { get; set; } = "";
    public string Time { get; set; } = "";
    public bool Ok { get; set; }
    public int StatusCode { get; set; }
}

/// <summary>GRCS /api/Cargo 库存查询响应（托盘与货物共用，靠 Code 前缀区分）。</summary>
public class CargoQueryResult
{
    public int Code { get; set; }
    public string? Message { get; set; }
    public CargoPagedData? Data { get; set; }
}

public class CargoPagedData
{
    public int TotalCount { get; set; }
    public List<CargoInventoryItem>? Records { get; set; }
}

public class CargoInventoryItem
{
    public int Id { get; set; }
    public string? Code { get; set; }
    public string? HomeStationMark { get; set; }
    public string? HomeStationScene { get; set; }
    public string? HomeCargoAreaName { get; set; }
    public bool IsLoaded { get; set; }
    public string? RobotId { get; set; }
    public bool IsLocked { get; set; }
    public string? CurrentStationCode { get; set; }
    public string? CurrentCargoAreaName { get; set; }
    public string? CurrentOrderId { get; set; }

    public bool IsPallet() => Code?.Contains("Container", StringComparison.OrdinalIgnoreCase) ?? false;
    public bool IsCargo() => Code?.Contains("Cargo", StringComparison.OrdinalIgnoreCase) ?? false;
}

/// <summary>发送给 GRCS 的任务组（/api/v1/task_receive）。</summary>
public class WcsTaskGroup
{
    public string GroupId { get; set; } = "";
    public string MsgTime { get; set; } = "";
    public int PriorityCode { get; set; }
    public string Warehouse { get; set; } = "";
    public List<WcsTaskItem> Tasks { get; set; } = [];
}

public class WcsTaskItem
{
    public string TaskId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public List<string> StationCode { get; set; } = [];
    public List<string> AreaCode { get; set; } = [];
}

/// <summary>车辆任务请求（/api/RawOrder/ChangeFloor，MOVE_ONLY 纯移动）。</summary>
public class VehicleOrderRequest
{
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public string SceneName { get; set; } = "";
    public string OrderType { get; set; } = "MOVE_ONLY";
    public string OrderId { get; set; } = "";
    public string OrderName { get; set; } = "";
    public string? VehicleName { get; set; }
    public int Priority { get; set; }
    public List<string> StationCodes { get; set; } = [];
    public string ErrorCode { get; set; } = "";
}

/// <summary>站点锁条目（流程终点任务 FINISHED 后释放）。</summary>
public class StationLockEntry
{
    public string TaskId { get; set; } = "";
    public string? Time { get; set; }
}

/// <summary>地图上传请求（POST /api/wcs/map/upload）。</summary>
public class MapUploadDto
{
    public string SavedAt { get; set; } = "";
    public int PathsCount { get; set; }
    public List<MapStationLite> Stations { get; set; } = [];
}

/// <summary>日志条目接口约定（前端增量轮询用 sinceId，与 task-stages 同一收敛模式）。</summary>
public class AutoLogEntryDto : LogEntryDto { }
