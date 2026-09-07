# PackagingStationPC

Origin Kit Gen1 包装工位上位机。该工程与 `spaceTestPC` 完全独立，不引用或修改原项目。

## 功能

- 采集眼镜和左右手环版本，不读取主机应用或主机系统版本。
- 按 `G1 + 包装厂代码 + 两位年份 + 两位周次 + 六位流水号` 生成外包装 SN。
- 使用 SQLite 事务预留 SN，防止程序重启、上传重试或重复设备造成重号。
- 工厂系统上传支持 `mock` 和 SwiftMOM 三步过站实现。
- 标签打印支持 `mock`、Windows 自绘和 BarTender 2022 模板三种实现。
- 上传和打印分别记录状态，可以单独联调和重试。

示例：`G1F2632000001`。

## 版本采集

眼镜查询协议作为 EXE 内嵌资源发布。查询时程序通过 ADB 将对应查询程序临时推送到设备 `/tmp`，执行完成后立即删除。设备不需要预装查询脚本，查询文件也不会持久保留：

- 眼镜：打开 `/dev/cam0_acm1` 或 `/dev/ttyACM1`，使用 921600 8N1，发送 YCTC UART v3 `0xA4` 版本请求，最多竞争读取 8 轮。
- 手环：由 C# 上位机直接枚举 Windows COM 口，优先选择 USB VID/PID `303A:1001` 的当前串口；若注册表无法提供 USB 身份才回退为逐个串口探测。串口参数为 2000000 8N1、DTR/RTS 开启；先发送 `hand` 判定左右手，再发送 `version`，只保存其中的版本号。命令按字节发送并处理 `>>>>>` 控制台提示符，不经过 ADB，也不依赖 Python。

眼镜查询仍要求设备端有 `python3`，并且 ADB 用户能够写入 `/tmp` 和访问眼镜串口。手环查询只要求 Windows 已正确安装手环的 USB 串口驱动，不需要设备端环境。

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

`mode` 为 `swiftmom` 时，每次上传依次调用 `clientLogin`、`checkRoute` 和 `createRoute`。外包装 SN 作为 `pcbSeq`；只有 `checkRoute` 返回 `msgId=0` 才会写入过站数据。眼镜和左右手环版本会序列化为紧凑 JSON 字符串写入 `remark`。`mode` 为 `mock` 时只执行本地模拟，不访问 MES。

调试打印机：

```json
"printer": {
  "mode": "bartender",
  "printerName": "生产标签打印机",
  "templatePath": "D:\\BarTenderTemplates\\OriginKitGen1.btw",
  "snDataSourceName": "PackageSn",
  "barTenderSdkAssemblyPath": "",
  "copies": 1,
  "printTimeoutSeconds": 30,
  "labelWidthMm": 60,
  "labelHeightMm": 35
}
```

`bartender` 模式需要安装 BarTender 2022 SDK，并在 `.btw` 模板中建立名为 `PackageSn` 的命名数据源。上位机将当前 SN 写入该数据源，标签中的 QR Code 或 Code 128 类型由模板决定。`barTenderSdkAssemblyPath` 留空时自动查找 `Seagull.BarTender.Print.dll`；也可填写 DLL 的绝对路径。打印机仍可在“打印调试”页临时选择。`windows` 模式保留原有 WPF 自绘标签打印，`mock` 模式不发送真实任务。

生产模式建议保持：

```json
"workflow": {
  "requireUploadBeforeNext": true,
  "requirePrintBeforeNext": true
}
```

## 数据

数据库和日志默认位于：

```text
%LOCALAPPDATA%\OriginFlow\PackagingStationPC\
```

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
