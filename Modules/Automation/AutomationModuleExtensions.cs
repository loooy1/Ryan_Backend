using GrcsBackend.Modules.Automation.Services;

namespace GrcsBackend.Modules.Automation;

/// <summary>
/// Automation 模块依赖注入注册（Skill E：自动化/地图/台账/信号下沉到后端）。
/// 注册顺序注意：HostedService 需要以单例方式同时被控制器注入与宿主启动。
/// </summary>
public static class AutomationModuleExtensions
{
    public static IServiceCollection AddAutomationModule(this IServiceCollection services)
    {
        services.AddHttpClient();   // IHttpClientFactory（GrcsHttpClient 用）
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
        return services;
    }
}
