# PackagingStationPC

Origin Kit Gen1 包装工位上位机。该工程与 `spaceTestPC` 完全独立，不引用或修改原项目。

## 功能

- 采集眼镜和左右手环版本，不读取主机应用或主机系统版本。
- 接收 USB HID 扫码枪输入，将外包装盒码直接作为 SwiftMOM `pcbSeq`。
- 扫码后自动采集设备 SN 和版本，并使用 SQLite 事务保存盒码、设备与版本绑定关系。
- 工厂系统上传支持 `mock` 和 SwiftMOM 三步过站实现。
- 标签打印当前已从界面和运行流程中停用，保留原适配源码供后续需要时恢复。
- 上传状态独立记录，可以联调和重试。

示例：`G1F2632000001`。

## 扫码流程

扫码枪按标准 USB 键盘模式接入。程序收到 Enter、换行或输入停止 500ms 后提交盒码，随后自动读取设备 SN 和版本、保存本地绑定、调用 SwiftMOM 三步接口。下一次扫码会自动清空上一台的界面结果并开始新流程，SQLite 历史记录不会删除。盒子或眼镜的必需 SN/版本、左右手环 SN 任一缺失时，界面以红字提示并停止绑定和 MES 上报；当前手环固件不支持版本命令时允许版本为空。已成功上传的盒码与设备绑定禁止修改。

## 版本采集

眼镜查询协议作为 EXE 内嵌资源发布。查询时程序通过 ADB 将对应查询程序临时推送到设备 `/tmp`，执行完成后立即删除。设备不需要预装查询脚本，查询文件也不会持久保留：

- 眼镜：通过 ADB shell 打开 `/dev/cam0_acm1` 或 `/dev/ttyACM1`，使用 921600 8N1 传输原始字节；C# 构造并解析 YCTC UART v3 帧，发送 `0xA4` 读取版本，发送 `0xB5` 并解析 `0xB6/0xB7` 读取 SN。
- 手环：由 C# 上位机直接枚举 Windows COM 口，优先选择 USB VID/PID `303A:1001` 的当前串口；若注册表无法提供 USB 身份才回退为逐个串口探测。串口参数为 2000000 8N1、DTR/RTS 开启；先发送 `hand` 判定左右手，再发送 `version`，只保存其中的版本号。命令按字节发送并处理 `>>>>>` 控制台提示符，不经过 ADB，也不依赖 Python。

眼镜查询不依赖 Python；YCTC 构帧、CRC 和响应解析全部由 C# 完成。设备端只需提供 `stty`、`timeout`、`base64`、`dd` 等基础命令，并允许访问眼镜串口。手环查询要求 Windows 已正确安装手环的 USB 串口驱动。

## 配置

配置文件为应用输出目录中的 `appsettings.json`。

调试工厂接口：

```json
"factorySystem": {
  "mode": "swiftmom",
  "baseUrl": "http://MES服务器:端口/swiftmom/api",
  "clientLoginPath": "clientLogin",
  "checkRoutePath": "checkRoute",
  "createRoutePath": "createRoute",
  "userNo": "MES用户名",
  "password": "MES密码",
  "prodNo": "工单号",
  "stationNo": "当前站位编号",
  "retest": "0",
  "timeoutSeconds": 15
}
```

`mode` 为 `swiftmom` 时，每次上传依次调用 `clientLogin`、`checkRoute` 和 `createRoute`。外包装 SN 作为 `pcbSeq`；只有 `checkRoute` 返回 `msgId=0` 才会写入过站数据。眼镜和左右手环版本会序列化为紧凑 JSON 字符串写入 `rmk1`，`remark` 保持为空。`mode` 为 `mock` 时只执行本地模拟，不访问 MES。

`prodNo` 和 `stationNo` 仅在应用目录的 `appsettings.json` 中配置，不在生产界面显示。日志记录扫码、版本值、本地绑定，以及 SwiftMOM 各阶段的 URL、脱敏请求 JSON、HTTP 状态和脱敏响应 JSON。开发运行时日志位于工程根目录的 `logs\packaging-station.log`；发布后位于 EXE 目录的 `logs\packaging-station.log`。密码和 token 不写日志。

打印配置当前固定为停用模式：

```json
"printer": {
  "mode": "mock",
  "printerName": "",
  "templatePath": "D:\\BarTenderTemplates\\OriginKitGen1.btw",
  "snDataSourceName": "PackageSn",
  "barTenderSdkAssemblyPath": "",
  "copies": 1,
  "printTimeoutSeconds": 30,
  "labelWidthMm": 60,
  "labelHeightMm": 35
}
```

打印服务当前不在应用启动和生产工作流中实例化，界面也不提供打印入口。`PrinterServices.cs` 中仅保留原适配源码。

生产模式建议保持：

```json
"workflow": {
  "requireUploadBeforeNext": true,
  "requirePrintBeforeNext": false
}
```

## 数据

数据库默认位于：

```text
%LOCALAPPDATA%\OriginFlow\PackagingStationPC\
```

日志位于工程或发布目录的 `logs\packaging-station.log`。

`production_records` 分别保存 `upload_status` 和 `print_status`。流水号在预留 SN 时立即占用；失败重试使用原 SN，不会重新取号。操作员手动设置下一流水号会写入 `counter_events`。

当前使用 ADB transport serial 作为设备去重键。正式部署前应确认该值在每台产品上稳定且唯一；如果产线有独立硬件 UID，应将其作为设备去重键并将 ADB serial 仅作为连接标识。

## 构建与测试

```powershell
dotnet build .\PackagingStationPC.slnx -c Release -p:NuGetAudit=false
dotnet test .\PackagingStationPC.slnx -c Release -p:NuGetAudit=false
```

运行文件：

```text
PackagingStationPC.App\bin\Release\net10.0-windows\PackagingStationPC.exe
```
