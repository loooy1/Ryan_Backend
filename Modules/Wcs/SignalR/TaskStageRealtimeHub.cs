using GrcsBackend.Modules.Wcs.Services;
using Microsoft.AspNetCore.SignalR;

namespace GrcsBackend.Modules.Wcs.SignalR;

/// <summary>
/// 任务阶段事件实时推送 Hub（/hubs/task-stages）。
/// 前端不再轮询 /api/wcs/task-stages，改为订阅本 Hub：
/// 新事件到达（TaskStageService.Record）即时广播 EventAdded，
/// 删除广播 TaskRemoved，清空广播 EventsReset(空表)。
/// 连接建立时先回放当前事件快照（EventsReset），保证断线重连后缓存不丢。
/// </summary>
public class TaskStageRealtimeHub : Hub
{
    private readonly ITaskStageService _stages;

    public TaskStageRealtimeHub(ITaskStageService stages) => _stages = stages;

    public override async Task OnConnectedAsync()
    {
        // 回放当前快照（等价于旧轮询的全量首拉），让新连接立即有数据
        await Clients.Caller.SendAsync("EventsReset", _stages.GetEvents());
        await base.OnConnectedAsync();
    }
}