namespace PackagingStationPC.ViewModels;

/// <summary>界面右侧"设备动作"卡片里的一行：动作标识、动作名、结果、实际下发的命令与结果说明。</summary>
public sealed record DeviceActionRowViewModel(
    string Key,
    string Name,
    string Status,
    string StatusBrush,
    string Command,
    string Detail);
