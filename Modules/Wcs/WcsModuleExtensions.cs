using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Modules.Wcs.Automation.Services.TWD;
using GrcsBackend.Modules.Wcs.Console.Services;
using GrcsBackend.Modules.Wcs.Proxy.Services;

namespace GrcsBackend.Modules.Wcs;

/// <summary>
/// Wcs 总模块的依赖注入注册（Wcs 为总目录，下面分 Automation / Proxy / Console / Realtime 子模块）。
/// 注册顺序注意：HostedService 需要以单例方式同时被控制器注入与宿主启动。
/// </summary>
public static class WcsModuleExtensions
{
    public static IServiceCollection AddWcsModule(this IServiceCollection services)
    {
        services.AddHttpClient();   // IHttpClientFactory（GrcsHttpClient 用）

        // ── Automation（自动下发/信号/台账/日志/数据基础设施）──
        services.AddSingleton<AutomationDb>();
        services.AddSingleton<AutomationLogService>();
        services.AddSingleton<MapStoreService>();
        services.AddSingleton<RangeConfigService>();
        services.AddSingleton<WcsSettingsService>();
        services.AddSingleton<CargoCodeStore>();
        services.AddSingleton<StationLockStore>();
        services.AddSingleton<LedgerStore>();
        services.AddSingleton<SignalConfirmStore>();
        services.AddSingleton<GrcsHttpClient>();
        // 轮询/批量互斥闸（多标签页也能保证只有一个在执行）
        services.AddSingleton<AutomationGate>();

        // 自动化轮询服务：单例 + IHostedService 双注册（控制器可注入操纵）
        services.AddSingleton<AutoRunHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<AutoRunHostedService>());
        // 批量容器执行器（非后台，仅控制器触发）
        services.AddSingleton<ContainerTaskRunner>();
        // 信号自动放行：宿主启动即常驻（后端唯一，取代前端 leader 模式）
        services.AddSingleton<SignalAutoHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SignalAutoHostedService>());

        // ── Console（控制台/阶段/出入申请/台账/地图）──
        // 准入决策状态必须跨请求共享（GRCS 循环重发 + 管理接口查询），用 Singleton
        services.AddSingleton<IAdmittanceService, AdmittanceService>();
        // 任务阶段事件同样跨请求共享（GRCS 上报 + 前端轮询），用 Singleton
        services.AddSingleton<ITaskStageService, TaskStageService>();

        return services;
    }
}