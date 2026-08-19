using System.Net.Http.Json;
using System.Text.Json;
using GrcsBackend.Modules.Wcs.Automation.Models;

namespace GrcsBackend.Modules.Wcs.Proxy.Services;

/// <summary>
/// GRCS 核心后端（8224）HTTP 客户端：任务下发 / 车辆任务 / 库存查询 / 出站信号。
/// baseUrl 每调用传入（来自 WcsSettingsService，前端连接设置可改）。
/// </summary>
public class GrcsHttpClient
{
    private readonly IHttpClientFactory _factory;

    public GrcsHttpClient(IHttpClientFactory factory) => _factory = factory;

    private HttpClient NewClient()
    {
        var c = _factory.CreateClient();
        c.Timeout = TimeSpan.FromSeconds(10);
        return c;
    }

    /// <summary>任务组下发（/api/v1/task_receive）。</summary>
    public Task<(bool Ok, int StatusCode, string Json)> SendTaskGroupAsync(string baseUrl, WcsTaskGroup payload)
        => PostAsync($"{baseUrl.TrimEnd('/')}/api/v1/task_receive", payload);

    /// <summary>车辆任务（/api/RawOrder/ChangeFloor，MOVE_ONLY 纯移动）。</summary>
    public Task<(bool Ok, int StatusCode, string Json)> SendVehicleOrderAsync(string baseUrl, VehicleOrderRequest payload)
        => PostAsync($"{baseUrl.TrimEnd('/')}/api/RawOrder/ChangeFloor", payload);

    /// <summary>库存查询（/api/Cargo，支持编码/场景/锁定过滤 + 分页）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> QueryCargoInventoryAsync(string baseUrl, string scene,
        string? code = null, string? locked = null, int pageNo = 1, int pageSize = 2000)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/Cargo?pageNo={pageNo}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(code)) url += $"&SearchContextParams[Code]={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrWhiteSpace(scene)) url += $"&SearchContextParams[HomeStationScene]={Uri.EscapeDataString(scene)}";
        if (!string.IsNullOrWhiteSpace(locked)) url += $"&SearchContextParams[IsLocked]={locked}";
        try
        {
            var resp = await NewClient().GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    /// <summary>模拟生成容器入库（GET /AutoContainerEnter，场景名取设置）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> AutoContainerEnterAsync(string baseUrl, string sceneName,
        string prefix = "container", int num = -1, int floor = -1, int type = 1)
    {
        var url = $"{baseUrl.TrimEnd('/')}/AutoContainerEnter?sceneName={Uri.EscapeDataString(sceneName)}"
            + $"&prefix={Uri.EscapeDataString(prefix)}&num={num}&floor={floor}&type={type}";
        try
        {
            var resp = await NewClient().GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    /// <summary>地图 zip 下载（GET /api/Map/GetMap，场景名取设置）。</summary>
    public async Task<(bool Ok, int StatusCode, byte[] Bytes, string Error)> GetMapBytesAsync(string baseUrl, string sceneName)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/Map/GetMap?sceneName={Uri.EscapeDataString(sceneName)}&getTypes=feMap";
        try
        {
            var resp = await NewClient().GetAsync(url);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, bytes, "");
        }
        catch (Exception ex) { return (false, 0, Array.Empty<byte>(), ex.Message); }
    }

    // ── 出站信号（WCS → GRCS，信号自动放行用）──

    public Task<(bool Ok, int StatusCode, string Json)> SendContainerReadyAsync(string baseUrl, object payload)
        => PostAsync($"{baseUrl.TrimEnd('/')}/api/v1/container_ready", payload);

    public Task<(bool Ok, int StatusCode, string Json)> SendContainerRemoveAsync(string baseUrl, object payload)
        => PostAsync($"{baseUrl.TrimEnd('/')}/api/v1/container_remove", payload);

    public Task<(bool Ok, int StatusCode, string Json)> SendOperationFinishAsync(string baseUrl, object payload)
        => PostAsync($"{baseUrl.TrimEnd('/')}/api/v1/container_operation_finish", payload);

    private async Task<(bool Ok, int StatusCode, string Json)> PostAsync<T>(string url, T payload)
    {
        try
        {
            var resp = await NewClient().PostAsJsonAsync(url, payload);
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    /// <summary>存活探测：GET 根路径，能拿到任意状态码即视为可达（2 秒短超时，供健康轮询）。</summary>
    public async Task<bool> PingAsync(string baseUrl)
    {
        try
        {
            var c = NewClient();
            c.Timeout = TimeSpan.FromSeconds(2);
            using var resp = await c.GetAsync(baseUrl.TrimEnd('/') + "/");
            return true;
        }
        catch { return false; }
    }
}
