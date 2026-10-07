# PackagingStationPC 设计文档

## 1. 文档目的

本文描述包装工位上位机的运行准备、软件结构、扫码生产流程、设备 SN/版本采集逻辑、SwiftMOM 协议对接、本地持久化、日志及故障处理。

系统处理的一套产品由以下设备组成：

| 设备 | 必需信息 | 连接方式 |
|---|---|---|
| 外包装盒 | 扫码内容 | USB HID 扫码枪 |
| 盒子主机 | SN、U-Boot、Kernel、RootFS 版本 | ADB |
| 眼镜 | SN、U-Boot、Kernel、RootFS、App 版本 | ADB 转眼镜 ACM |
| 左手环 | SN；版本尽力读取 | Windows USB 串口 |
| 右手环 | SN；版本尽力读取 | Windows USB 串口 |

当前不执行标签打印。打印服务源码保留，但不参与启动和生产流程。

## 2. 技术架构

- 开发语言：C#
- UI：WPF
- 目标框架：`.NET 10.0 Windows`
- 本地数据库：SQLite
- 扫码枪：USB HID 键盘模式
- 盒子和眼镜连接：ADB
- 手环连接：`System.IO.Ports.SerialPort`
- MES：HTTP JSON，SwiftMOM 三阶段接口

主要模块：

| 模块 | 职责 |
|---|---|
| `MainViewModel` | 扫码流程编排、采集校验、绑定、上传和界面状态 |
| `ScannerService` | 扫码字符串规范化 |
| `DeviceVersionService` | 盒子、眼镜、手环采集聚合 |
| `AdbClient` | ADB 设备枚举和 shell 命令执行 |
| `GlassesAcmService` | YCTC v3 构帧、CRC、眼镜版本和 SN 解析 |
| `BandSerialVersionService` | COM 口枚举、左右手识别、SN/版本读取 |
| `SqliteProductionRepository` | 绑定关系、上传状态和生产历史持久化 |
| `SwiftMomFactoryUploadService` | 登录、过站检查和过站写入 |
| `AppLogger` | 文件日志串行写入 |

## 3. 开始使用

### 3.1 环境准备

1. 安装 .NET 10 SDK 或匹配的 Desktop Runtime。
2. 确保 `adb` 可以从系统 `PATH` 调用，或在配置中填写完整路径。
3. 安装盒子 ADB 驱动。
4. 安装手环 USB 串口驱动。
5. 将扫码枪设置为 USB HID 键盘模式，并配置扫码结束符 Enter；即使没有结束符，程序也会在停止输入 500ms 后提交。
6. 确保计算机可以访问 SwiftMOM 服务地址。

### 3.2 配置文件

配置文件位于程序输出目录：

```text
PackagingStationPC.App\bin\Debug\net10.0-windows\appsettings.json
```

生产发布时位于 EXE 同级目录。`prodNo` 和 `stationNo` 只允许通过该文件配置，不在上位机界面显示。

关键配置示例：

```json
{
  "adb": {
    "path": "adb",
    "deviceSerial": "",
    "commandTimeoutSeconds": 10,
    "peripheralTimeoutSeconds": 45,
    "requireCompleteVersionSet": false
  },
  "boxVersion": {
    "enabled": true,
    "checks": [
      { "field": "uboot", "minVersion": "0.1.1", "enabled": true, "blockOnFail": true },
      { "field": "kernel", "minVersion": "0.1.6", "enabled": true, "blockOnFail": true },
      { "field": "rootfs", "minVersion": "0.1.6", "enabled": true, "blockOnFail": true },
      { "field": "gen1App", "minVersion": "0.5.2", "enabled": true, "blockOnFail": true },
      { "field": "wlan0BtSwitch", "expected": "0", "enabled": true, "blockOnFail": true, "allowMissing": false }
    ]
  },
  "glassesVersion": {
    "enabled": true,
    "checks": [
      { "field": "uboot", "minVersion": "0.0.2", "enabled": true, "blockOnFail": true },
      { "field": "kernel", "minVersion": "0.0.7", "enabled": true, "blockOnFail": true },
      { "field": "rootfs", "minVersion": "0.0.6", "enabled": true, "blockOnFail": true },
      { "field": "app", "minVersion": "0.1.0", "enabled": true, "blockOnFail": true }
    ]
  },
  "bandVersion": {
    "enabled": true,
    "checks": [
      { "field": "leftVersion", "minVersion": "3.0.3.6", "enabled": true, "blockOnFail": true, "allowMissing": false },
      { "field": "rightVersion", "minVersion": "3.0.3.6", "enabled": true, "blockOnFail": true, "allowMissing": false }
    ]
  },
  "factorySystem": {
    "mode": "swiftmom",
    "baseUrl": "http://192.168.10.17:8080/swiftmom/api",
    "clientLoginPath": "clientLogin",
    "checkRoutePath": "checkRoute",
    "createRoutePath": "createRoute",
    "userNo": "MES用户名",
    "password": "MES密码",
    "prodNo": "工单号",
    "stationNo": "站位编号",
    "retest": "0",
    "timeoutSeconds": 15
  },
  "printer": {
    "mode": "mock"
  },
  "workflow": {
    "requireUploadBeforeNext": true,
    "requirePrintBeforeNext": false
  }
}
```

`factorySystem.mode=swiftmom` 时访问真实 MES；`mock` 只模拟成功，不访问 MES。

### 3.3 构建与运行

```powershell
dotnet restore .\PackagingStationPC.slnx -p:NuGetAudit=false
dotnet build .\PackagingStationPC.slnx -c Debug --no-restore
dotnet test .\PackagingStationPC.slnx -c Debug --no-restore
```

运行：

```powershell
.\PackagingStationPC.App\bin\Debug\net10.0-windows\PackagingStationPC.exe
```

### 3.4 程序版本号

编译时由 `GenerateBuildVersionFile` 目标执行 `git describe`（tags/always/dirty/long），
把 `tag + 距 tag 提交数 + 短 commit + 是否 dirty` 写入 `BuildVersionInfo.Describe`，
再由 `BuildVersion` / `GitVersionDescription` 解析。界面底部状态栏显示：

```text
版本: V1.0.0+1 (393da69-dirty)
```

鼠标悬停可看到完整的 tag、commit、距 tag 提交数和工作区状态；版本同时写入每次版本采集日志，
便于追溯现场运行的程序来自哪个 tag/commit。git 不可用或仓库无提交时显示“未知版本”。

发布前打 tag（`git tag V1.0.0`）后重新编译，版本号即为该 tag。

## 4. 整体生产流程

```text
上位机启动
  -> 初始化 SQLite
  -> 枚举当前 ADB 设备
  -> 等待扫描外包装盒码

扫码提交
  -> 清空上一台的界面状态（不删除数据库历史）
  -> 校验扫码内容
  -> 确认当前在线 ADB 设备
  -> 读取盒子 SN 和版本
  -> 读取眼镜 SN 和版本
  -> 枚举 COM 口并读取左右手环 SN/版本
  -> 校验上报必需字段
     -> 不完整：红字提示，停止绑定，禁止调用 MES
     -> 完整：SQLite 保存盒码与整套设备关系
  -> clientLogin
  -> checkRoute
     -> 失败：保存失败状态，显示错误，不调用 createRoute
     -> 成功：createRoute
  -> 更新 SQLite 上传结果
  -> 显示最终成功或失败
  -> 等待下一次扫码
```

程序不会因为一轮结束而关闭。再次扫码会自动清空旧的界面记录并启动新流程，不需要点击“下一台”。正在采集或上传时禁止重复触发。

程序不监听 ADB 热插拔并自动采集；采集只由扫码或人工点击“读取全部版本”触发。手动读取只用于检查设备信息，不会单独触发绑定或 MES 上报。

## 5. 设备信息获取逻辑

### 5.1 盒子

首先解析盒子产品 SN：

```sh
sudo vendor_storage -r VENDOR_SN_ID -t string
```

期望输出：

```text
[INFO] VENDOR_SN_ID:D1S724V30W2631000004
```

随后执行：

```sh
version
```

期望输出：

```text
uboot version:  v0.0.2-260824.095308-e4474f346
kernel version: v0.1.1-260903.050734-436e50b1d
rootfs version: 0.0.5
```

再读取盒子上 Gen1-APP（`gen1-app.service` 对应的 deb 包）的版本：

```sh
dpkg-query -W gen1-app
```

期望输出：

```text
gen1-app	0.5.0-20260920-36.3dc2a236
```

最后读取盒子上的无线/蓝牙切换开关：

```sh
cat /oem/config/wlan0_bt_switch
```

期望输出（要求为 `0`）：

```text
0
```

采集字段：`box.sn`、`box.uboot`、`box.kernel`、`box.rootfs`、`box.gen1App`、`box.wlan0BtSwitch`。
ADB transport serial 只用于选择连接，不作为产品 SN。

四条命令合并在同一条 shell 中执行（SN 需要 root 或 `sudo -n`，`dpkg-query` 只查包数据库不需要 root），
用 `__BOX_SN_RC__`、`__BOX_VER_RC__`、`__BOX_APP_RC__`、`__BOX_SW_RC__` 四个标记行分段并带回各自退出码，
避免多次 adb 往返。前三条中任一条失败都视为盒子采集失败（`Gen1-APP` 未安装时 `dpkg-query` 退出码非 0）。

`cat` 这条是例外：开关文件在部分盒子上可能不存在或不可读，读不到时**不判采集失败**，
只把 `wlan0BtSwitch` 留空，交给 5.4 的校验配置去判（默认 `allowMissing=false`，即读不到就算不合格）。
这样避免一个配置项把整轮采集（连 SN 一起）拖成失败。

### 5.2 眼镜

眼镜通过盒子的 `/dev/cam0_acm1` 访问；不存在时回退 `/dev/ttyACM1`。串口速率为 921600，YCTC UART 协议版本为 v3。

帧结构：

```text
Magic(53 59) + Version(03) + Type + Flag + Sequence(LE)
+ PayloadLength(LE) + Reserved + Payload + CRC16(LE)
```

CRC 使用 Modbus CRC16：初始值 `0xFFFF`，多项式 `0xA001`。

版本读取：

- 请求/响应类型：`0xA4`
- Payload：UTF-8 JSON
- 解析字段：`uboot`、`kernel`、`rootfs`、`app`

SN 读取：

- 请求：`SN_READ=0xB5`
- 成功响应：`SN_DATA=0xB6`
- 失败响应：`SN_STATUS=0xB7`
- `SN_DATA` Payload：`ver(1) + len(1) + UTF-8 SN`

协议构帧、CRC 和响应解析全部使用 C#。ADB shell 只调用设备已有的 `busybox microcom` 完成原始字节传输。单个请求最多重试 8 次。

采集字段：`glasses.sn`、`glasses.uboot`、`glasses.kernel`、`glasses.rootfs`、`glasses.app`。

### 5.3 左右手环

上位机从 Windows 注册表查找 USB VID `303A`（不限定 PID，现场同一只手环在不同固件/启动模式下 PID 会变）对应的 COM 口，用于**排优先级**。过滤结果**只排序不排除**：

- 命中 0 或 1 个口：一定凑不齐“一左一右”，直接退化为探测当前全部 COM 口；
- 命中 2 个及以上：先探测命中的口；若仍凑不齐一左一右，补测时把被过滤掉的口一并兜底探测。

这样即使注册表漏掉刚插入、挂在 Hub 上或 PID 不同的那只手环，也能在同一次扫码内读到。

串口参数：

```text
BaudRate: 2000000
DataBits: 8
Parity: None
StopBits: 1
Handshake: None
DTR: false
RTS: false
```

打开串口后依次发送：

1. `hand`：返回 `hand=l` 或 `hand=r`，这是左右归属的唯一依据，不按 COM 号判断。
2. `sn`：解析 `factory: [ok]sn=...`。
3. `version`：解析 `factory: [ok]version=...`。

示例：

```text
>>>>> hand
I (...) basic_info: hand=l
>>>>> sn
I (...) factory: [ok]sn=V61S1.0-C62100000042326
>>>>> version
I (...) factory: [ok]version=3.0.3.3
```

采集字段：

```text
band.leftSn
band.leftVersion
band.rightSn
band.rightVersion
```

左右手环 SN 必须读取。当前兼容部分固件不支持 `version` 命令的情况：版本读取失败时保存空字符串并在界面显示“未获取”，但不单独阻止上报。

左右配对是硬约束，必须满足“有且仅有一只左手环 + 有且仅有一只右手环，且两者 SN 不同”，左右归属只认 `hand` 命令返回值，不按 COM 号判断。以下情况判为不合法，手环组件置为 `Failed` 并清空左右 SN：

- 任一侧未读到 SN；
- 任一侧检测到多只同侧手环（例如两只左手环）；
- 左右 SN 相同（同一只手环被当成左右两只）。

#### 冷启动补测与超时约束

手环刚上电/刚插入时 USB CDC 往往还没就绪，第一次探测会整体落空、必须扫第二遍才成功。采集过程因此分两轮，并全程受预算约束：

1. 第一轮：并行探测候选串口（限流 4），开串口后等待 300ms，`hand` 最多 3 次（单次 1s）、`sn`/`version` 各最多 2 次（单次 1.5s）。
2. 第二轮（补测）：针对“没识别出手环”或“识别出手环但没读到 SN”的口再跑一轮；若仍凑不齐一左一右，同时把被 USB 过滤排除掉的口一并补上；打开失败的口（被占用/不存在）不再重试。
3. 同一个口保留信息更完整的那次探测结果（有 SN > 只识别出左右 > 失败）。

超时闸（任何一层到点都放弃当前探测并返回已有结果，不会无限等待）：

| 约束 | 取值 |
|---|---|
| 整轮总预算（含补测） | 10s |
| 单轮预算 | 5s |
| 剩余时间不足以补测 | < 2.5s 时跳过补测 |
| 单轮收尾宽限（驱动卡在 Open 上） | 500ms |

手环采集全程写日志，前缀 `[band]`，包含候选串口来源、每个口的开串口结果、每条命令每次重试的响应摘要与耗时、补测原因与最终判定；排查时看日志里的 `Query start` / `Probe round1` / `Retry` / `Probe round2` / `Query done` 这几行即可。

### 5.4 版本配置校验

这是一个**独立于采集的动作**：等盒子、眼镜、手环的版本号全部读完之后才执行，不参与也不改变任何采集过程。

盒子、眼镜、手环**各有一个配置段、各有一个开关**，形状完全一样，互不影响：

| 配置段 | 作用组件 | 可取字段 |
|---|---|---|
| `boxVersion` | 盒子 | `uboot`、`kernel`、`rootfs`、`gen1App`、`wlan0BtSwitch` |
| `glassesVersion` | 眼镜 | `uboot`、`kernel`、`rootfs`、`app` |
| `bandVersion` | 手环 | `leftVersion`、`rightVersion` |

每一项有两种判定方式，**二选一**：

- 填 `minVersion` —— 按最低版本比对（适用场景：版本号）；
- 填 `expected` —— 按精确值比对，trim 后必须完全相等（适用场景：开关位、配置项，如 `wlan0BtSwitch` 要求为 `0`）。

两者都填时以 `expected` 为准；两者都不填则该项跳过。

比对规则（`VersionChecker`）：

- **精确值（填了 `expected`）**：两侧 trim 后完全相等才算通过。开关位是精确匹配而不是"不低于"，`wlan0BtSwitch` 读到 `1` 即使比 `0` 大也不合格。
- **最低版本（填了 `minVersion`）**：只按 `x.y.z` 这样的数字点分串比较，**不比较 `-` 之后的构建信息**。比较前忽略前导 `v` / `V`，只取版本号开头的数字点分段：
  - `v0.0.8-260909` → `0.0.8`
  - `0.5.0-20260920-36.3dc2a236` → `0.5.0`
- 位数不同时缺失位按 0 处理，`0.1` 与 `0.1.0` 视为相等；
- 读到的版本 **>= 配置项里的 `minVersion`** 才算通过；
- 任一侧解析不出数字点分串时判为"无法比较"，按失败处理。

每个配置段（以 `boxVersion` 为例，另两段同构）：

| 字段 | 说明 |
|---|---|
| `enabled` | 组件总开关，**默认 `true`**，盒子/眼镜/手环三段都默认校验；只在临时停用时置 `false` |
| `checks[].field` | 要校验的字段名，取值见上表 |
| `checks[].minVersion` | 要求的最低版本，写成 `0.5.0` 这样的 `x.y.z` 即可 |
| `checks[].expected` | 要求的精确值，如 `wlan0BtSwitch` 配 `"0"`；填了它就不看 `minVersion` |
| `checks[].enabled` | 单条规则的开关，便于临时停用一项而不是整段删掉 |
| `checks[].blockOnFail` | 失败时是否阻断，默认 `true` |
| `checks[].allowMissing` | 该字段读不到时是否放行，默认 `false`（读不到即判失败） |

手环版本同样是硬要求：默认配置里 `bandVersion` 的 `allowMissing` 为 `false`，左右手环版本读不到即判失败并阻断。只有想让某个字段"读得到就比、读不到就算了"时才把它设为 `true`。

校验失败时：

- 该组件置为 `Failed`（`blockOnFail=false` 时置 `Warning`，只提示不阻断），`Message` 带上逐项明细并以固定前缀 `版本校验未通过：` 开头，例如
  `版本校验未通过：gen1App 版本 0.4.0 低于要求的最低版本 0.5.0`；
- 失败项的值清空（与手环左右配对失效时的处理一致），让第 6 节的"必需字段非空"门禁一并拦住；
- SN 类字段（`sn`、`leftSn`、`rightSn`）不清空，保证 `DeviceSerial`、左右配对判定与绑定关系不受影响；
- 进度条与界面提示同步显示未通过原因。

某个组件采集本身已经失败时不再叠加校验结论，避免把"读不到"误报成"版本不合格"。

## 6. 上报门禁

扫码自动流程在写数据库和访问 MES 之前执行字段校验：

| 组件 | 必需字段 |
|---|---|
| 盒子 | `sn`、`uboot`、`kernel`、`rootfs`、`gen1App` |
| 眼镜 | `sn`、`uboot`、`kernel`、`rootfs`、`app` |
| 手环 | `leftSn`、`rightSn` |

此外还有两项不受 `requireCompleteVersionSet` 开关影响、失败时同样禁止预留 SN 与上报的校验：

- 手环左右配对（一左一右、SN 互不相同）；
- 版本配置校验（见 5.4，盒子/眼镜/手环各一个开关，默认全关；开启后版本低于配置的最低版本即阻断）。

任何必需字段为空时：

- 界面使用红色字体显示缺失信息；
- 不保存新的盒码绑定；
- 不调用 `clientLogin`、`checkRoute` 或 `createRoute`；
- 日志保留设备命令和失败原因，供人工排查；
- 操作员可关闭占用串口的软件后手动读取，或重新扫码启动新流程。

## 7. SQLite 绑定规则

数据库默认位于：

```text
%LOCALAPPDATA%\OriginFlow\PackagingStationPC\packaging-station.db
```

`production_records` 保存：

- 外包装盒码；
- 盒子产品 SN；
- 完整设备快照 JSON；
- 产品和工位信息；
- MES 上传状态、消息及时间；
- 创建时间。

绑定约束：

- 外包装盒码唯一；
- 盒子产品 SN 唯一；
- 已成功上传的盒码与设备关系禁止修改；
- 失败或待上传记录允许后续纠正绑定；
- 新扫码只清空界面，不删除历史生产记录。

## 8. SwiftMOM 协议对接

### 8.1 登录

```http
POST /swiftmom/api/clientLogin
Content-Type: application/json
```

```json
{
  "userNo": "mss",
  "password": "******"
}
```

成功条件：HTTP 成功、`msgId=0` 且返回非空 `token`。后续请求通过 HTTP Header 携带：

```text
token: <登录返回的 token>
```

### 8.2 过站检查

```http
POST /swiftmom/api/checkRoute
```

```json
{
  "pcbSeq": "外包装盒扫码内容",
  "prodNo": "配置文件中的工单号",
  "stationNo": "配置文件中的站位编号",
  "retest": "0"
}
```

只有 `msgId=0` 才允许调用 `createRoute`。

### 8.3 写入过站记录

```http
POST /swiftmom/api/createRoute
```

```json
{
  "pcbSeq": "外包装盒扫码内容",
  "prodNo": "YC20260903001-YZ",
  "stationNo": "T01",
  "result": "PASS",
  "remark": "",
  "testItem": "",
  "userNo": "mss",
  "weight": 0,
  "packNo": "",
  "rmk1": "",
  "rmk2": "{内部设备信息JSON字符串}",
  "rmk3": "",
  "rmk4": "",
  "testDetail": []
}
```

`rmk2` 是外层 JSON 中的字符串。将该字符串反序列化后，结构如下：

```json
{
  "deviceSn": "D1S724V30W2631000004",
  "box": {
    "sn": "D1S724V30W2631000004",
    "uboot": "v0.0.2-260824.095308-e4474f346",
    "kernel": "v0.1.1-260903.050734-436e50b1d",
    "rootfs": "0.0.5"
  },
  "glasses": {
    "sn": "D1V804V31W263200004",
    "uboot": "V0.0.2-260822.141024-f7ee5ba46",
    "kernel": "V0.0.5-260822.141034-f7ee5ba46",
    "rootfs": "V0.0.4-260822.141051-f7ee5ba46",
    "app": "V0.0.7-260822.141149-f7ee5ba46"
  },
  "band": {
    "leftSn": "V61S1.0-C62100000042326",
    "leftVersion": "3.0.3.3",
    "rightSn": "V61M1.0-C621000000412EA",
    "rightVersion": "3.0.3.3"
  }
}
```

`deviceSn` 与 `box.sn` 当前相同，前者保留为整套设备的主去重标识。

## 9. 日志设计

开发环境日志：

```text
C:\Users\31239\work\PackagingStationPC\logs\packaging-station.log
```

发布环境日志：

```text
EXE所在目录\logs\packaging-station.log
```

日志内容包括：

- 扫码内容和当前 ADB 连接标识；
- 各组件状态、解析后的 SN/版本、来源和错误；
- SQLite 绑定结果；
- SwiftMOM 每阶段 URL；
- 脱敏后的请求 JSON；
- HTTP 状态、`msgId`、`msgStr` 和脱敏后的响应 JSON；
- 上传最终结果和异常堆栈。

密码和 token 始终替换为 `***`，不得写入日志。

## 10. 常见故障定位

### ADB 未找到

执行：

```powershell
adb devices -l
```

确认设备状态为 `device`。多台 ADB 在线时需要在界面选择本工位设备。

### 盒子 SN 或版本失败

分别执行：

```powershell
adb shell "sudo vendor_storage -r VENDOR_SN_ID -t string"
adb shell version
```

检查输出是否包含预期字段。

### 眼镜读取失败

确认 `/dev/cam0_acm1` 或 `/dev/ttyACM1` 存在，当前用户有串口访问权限，并检查日志中的消息类型、重试和 CRC 结果。

### 手环没有显示

1. 先看 `logs/packaging-station.log` 里 `[band]` 开头的行（`Query start` / `Probe round1` / `Retry` / `Probe round2` / `Query done`），确认是端口打不开、`hand` 无响应、还是 `sn` 无响应。
2. 确认 Windows 设备管理器存在两个手环 COM 口。
3. 关闭串口调试助手和其他占用 COM 口的软件。
4. 确认串口参数为 2000000 8N1、无流控、DTR/RTS 关闭。
5. 手工执行 `hand`，确认两只设备分别返回 `l` 和 `r`。
6. 执行 `sn` 和 `version`，对照日志中的响应。

`Access to the path 'COMx' is denied` 表示端口被其他进程占用，不是解析失败。

### MES 不上报

优先查看红色界面提示和日志：

- 必需设备信息缺失时，程序按设计不访问 MES；
- `checkRoute msgId != 0` 时不调用 `createRoute`；
- `createRoute msgId != 0` 时本地记录标记为失败并保留服务端消息。
