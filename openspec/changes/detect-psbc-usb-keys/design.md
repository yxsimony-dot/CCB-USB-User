## Context

参见 `proposal.md`。当前应用已经具备银行下拉选择和运行时切换骨架，建设银行使用 `LauncherProfile`、`UsbDeviceMatcher`、`WindowsUsbDeviceMonitor` 和 `UsbKeyPresenceObserver` 完成检测。邮储银行在 `BankFlowCatalog` 中仍没有 profile，因此选择后不会创建运行时。

现有 Windows 快照能够把 USB 节点与其后代 CD-ROM、卷标和文件系统关联，但 `UsbDeviceDescriptor` 只表达静态设备属性，没有厂商探测结果、环境状态或 Token 身份。现有元数据重试也以建设银行布尔匹配为中心。

对“邮储银行网银助手”的只读调查显示，它按五个厂商拆分发现插件和 Token API：

| 厂商族 | 发现入口 | Token API 或邮储环境证据 |
| --- | --- | --- |
| 飞天 / EnterSafe | `GetSnDll_FT.dll` | `InterfaceForPSBC.dll`、`InterPass3000CSPCompany for PSBC V1.0` |
| 华大智宝 / HDZB | `GetSnDll_HD.dll` | `HDZBUkeyAPI.dll`、`HDZB_Service_PSBC` |
| 天地融 / Tendyron | `GetUKeySN.dll` | `TDR_PSBC_Dll.dll`、`OnKey Service _PSBC`、`Tendyron OnKey193 CSP PSBC v1.0` |
| 深圳文鼎创 / Excelsecu | `GetSnDll_WDC.dll` | `WDCPSBC.dll`、`Excelsecu eBank CSP for PSBC` |
| 握奇 / WatchData | `GetSnDll_WQ.dll` | `WDPSBC.dll`、`Watchdata PSBC CSP` |

这些接口普遍提供 `JudgeUkey`、`GetTokenName`、`GetTokenSN`、`DriversInstalled`、`JudgeServiceState` 等能力。当前可实机确认的样本是天地融设备 `VID_1780&PID_0401`，其关联介质为 `TENDYRON U-DISK`、卷标为 `PSBC`。

主程序目标平台为 x64，而上述厂商 DLL 为 x86。主进程不能直接加载这些 DLL；同时这些文件属于第三方专有组件，不能默认复制进发布包。

## Goals / Non-Goals

**Goals:**

- 建立统一的邮储检测结果和五个独立厂商适配器边界。
- 让检测状态能够表达设备不存在、设备存在但环境不可用、设备及环境可用。
- 允许银行识别规则重叠，以用户当前选择的银行作为唯一运行路由。
- 复用现有物理设备关联、取消、generation/revision 和运行时清理机制。
- 使厂商探测可以用离线夹具和可注入探测器测试，并为后续实机样本验收保留入口。

**Non-Goals:**

- 本变更不实现邮储网页登录、PIN 自动填写、流水或回单导出。
- 本变更不复制邮储助手界面，也不启动或操控邮储助手。
- 本变更不在主 x64 进程中直接加载 x86 厂商 DLL。
- 本变更不把一个 Key 强制归属到唯一银行。
- 本变更不初始化、格式化或修改 Token，也不提交 PIN。

## Decisions

### 1. 用户选择的银行是唯一运行路由

银行选择发生在设备语义判断之前。每个 `BankFlowDefinition` 将拥有独立的 profile、检测策略和运行时工厂。初始化时只构造当前银行所需的监听器、匹配器、认证和浏览器依赖。

```text
                         +--> CCB detector --> CCB runtime
physical USB snapshot --|
                         +--> PSBC detector -> PSBC runtime

selected bank ----------------^
only the selected branch is constructed and subscribed
```

一个设备可以被两个检测器分别判为匹配。应用不建立全局的“设备属于哪个银行”分类器，也不按匹配优先级自动切换银行。这样可以保留共享硬件族，同时满足用户显式选择流程的要求。

切换银行继续采用现有顺序：撤销当前 session，递增 revision，释放 presence observer 和 runtime，然后为新银行初始化。所有异步回调在发布前同时校验 session、revision 和当前银行。

替代方案是让所有银行监听器常驻并在回调时过滤。该方案会增加后台厂商调用、资源占用和陈旧回调竞争，因此不采用。

### 2. 邮储检测使用三态结果而不是布尔值

新增银行无关的检测结果模型，至少包含：

- `Status`: `Absent`、`PresentButEnvironmentUnavailable`、`Ready`
- `VendorFamily`: 五个已知厂商之一，未知时为空
- `TokenIdentity`: 厂商接口或系统提供程序能够安全读取时返回，不能读取时为空
- `DiagnosticCode`: 驱动缺失、服务停止、CSP 缺失、探测失败等稳定代码
- `PhysicalInstanceId`: 当前插入周期的 USB 节点标识，只用于生命周期关联

现有界面仍可把 `Ready` 映射为“USB Key 已连接”，但环境不可用需要显示为可诊断状态，不能与未插入混为一谈。Token 身份不得写入普通日志；诊断只记录厂商、状态和错误代码。

替代方案是继续返回 `bool` 并把所有故障视为未插入。该方案无法指导用户安装环境，也会让驱动故障触发错误的插拔生命周期，因此不采用。

### 3. 五个厂商适配器共享只读探测契约

建立 `IPsbcUsbKeyProbe` 类似的内部契约，每个适配器负责：

1. 根据当前物理 USB 快照判断是否存在本厂商候选设备。
2. 检查邮储专用服务、CSP、注册表或驱动环境。
3. 使用无副作用方式确认实体 Token 当前存在。
4. 在安全可用时返回 Token 名称或稳定身份。

适配器顺序不表示银行归属优先级。多个适配器同时报告候选时，聚合器按实体 USB 节点去重；只有活跃银行的聚合器运行。`TokenInitialize`、PIN 校验、证书签名等写入或认证操作不属于探测契约。

厂商初始证据如下：

- 飞天：Token EasyPlay/Token 2000Auto/InterPass3000 系列介质和邮储 InterPass CSP。
- 华大：已知 `VID_1677&PID_6004`、`HDZB_Service_PSBC` 和 `PSBC_HDZB_USBKey2G` 标记。
- 天地融：已知 `VID_1780&PID_0401`、`TENDYRON U-DISK`、`PSBC` 卷标、邮储 OnKey 服务/CSP。
- 文鼎创：`ven_swukey`、`ven_usbdisk`、`prod_composite_token`、`prod_scsitoken` 描述和邮储 Excelsecu CSP。
- 握奇：WatchData 厂商探测和 `Watchdata PSBC CSP`。

VID/PID、介质名称和卷标只用于候选筛选。`Ready` 必须有当前实体 Token 的活跃证据；不能因为厂商软件已安装就报告 Ready。

### 4. 基础实现不依赖邮储助手专有 DLL

第一阶段适配器优先使用 Windows 设备树、服务控制管理器、注册表和已注册加密提供程序完成候选识别与环境检查，并通过系统支持的提供程序查询确认 Token 可用。

若某个厂商无法通过受支持的系统接口可靠确认实体 Token 或读取所需身份，则保持 `PresentButEnvironmentUnavailable` 或厂商特定的不可用诊断，不伪造 Ready。需要完全复刻 `JudgeUkey/GetTokenSN` 时，应另行批准一个独立 x86 broker：

- broker 只加载本机已安装目录中的厂商 DLL，不把 DLL 打包进本产品；
- x64 主程序通过版本化 IPC 请求只读探测；
- broker 校验 DLL 路径、文件签名或哈希、调用超时和返回长度；
- broker 崩溃或超时只影响当前厂商探测，不影响主应用；
- 是否允许此依赖和分发方式需要单独确认。

替代方案是在主程序改为 x86 或 AnyCPU。该方案会扩大 WebView2、现有 native broker 和发布包回归范围，因此不采用。

### 5. 将元数据重试从 CCB 布尔匹配泛化为策略

`WindowsUsbDeviceMonitor` 保留去抖、generation、取消和有限重试机制，但重试条件由当前银行检测策略提供。邮储策略可以在以下情况请求重试：

- USB 节点已出现，但 CD-ROM 或卷标尚未挂载；
- 厂商服务刚启动或加密提供程序尚未可见；
- 厂商探测暂时返回忙或未就绪。

重试使用完整新快照，不缓存上次 Ready 作为当前证据。实体节点真实消失时立即发布 Absent；同一节点的短暂元数据缺失在重试窗口内不制造新的插拔周期。

### 6. 邮储 profile 与建设银行 profile 完全独立

邮储银行获得独立 profile 文件和 `psbcUsbKey` 检测策略。配置验证器明确识别该策略，不复用建设银行 URL、证书、CSP、PIN 目标名或浏览器会话设置。

银行切换时，旧 profile 创建的所有对象均由旧 session 拥有并释放。新银行初始化失败时保持在所选银行并显示错误，不回退运行另一个银行的 profile。

### 7. 验证分为离线契约测试和实机验收

离线测试覆盖：

- 五个厂商适配器的候选、环境缺失、Ready 和异常结果；
- 当前天地融样本；
- 同一个设备同时匹配 CCB 和 PSBC；
- 只构造当前银行运行时；
- 切换期间的取消、释放和迟到回调；
- 元数据延迟、真实拔出和重复通知；
- 不调用初始化、PIN 或签名接口。

实机验收先使用当前天地融 Key。其余四个厂商在获得样本后逐一记录只读设备快照并验证插入、环境缺失、Ready、拔出和重新插入。没有实机样本的适配器只能视为契约实现完成，不能宣称硬件验收完成。

## Risks / Trade-offs

- [其余四个厂商暂无实机样本] -> 先使用助手证据和离线夹具实现契约，并把每个厂商的实机验收列为独立任务。
- [驱动已安装可能被误判为 Key 已插入] -> Ready 必须包含当前实体 Token 的活跃证据，安装状态只决定环境诊断。
- [厂商服务或提供程序查询可能阻塞] -> 所有探测支持取消、超时和异常隔离，不在 UI 线程执行。
- [共享 VID/PID 会让两个检测器都匹配] -> 这是允许行为；只构造当前选择银行的运行时。
- [第三方 DLL 接口或许可不稳定] -> 基础范围不直接依赖专有 DLL，确有需要时单独设计和批准 x86 broker。
- [银行切换时旧回调污染新状态] -> 继续使用 session、revision、当前银行三重校验，并在新运行时初始化前完成旧 owner 释放。

## Migration Plan

1. 先增加检测结果模型、邮储策略配置和五厂商适配器契约，保持邮储业务仍不可启动。
2. 增加离线夹具与单元测试，包含当前天地融样本和 CCB/PSBC 重叠匹配。
3. 泛化 Windows 监听重试并把邮储 profile 接入现有银行运行时工厂。
4. 使用当前天地融 Key 完成插入、环境、切换、拔出和重新插入验收。
5. 获得其余厂商样本后逐个完成实机验收；未验收厂商在诊断中保持明确标记。
6. 若发布后邮储检测存在阻塞或误报，可将邮储 flow 恢复为未配置状态，不影响建设银行 profile 和运行时。

## Open Questions

- 飞天、华大、文鼎创和握奇实机样本的可获得时间尚未确定；这不改变接口设计，但决定这些厂商何时可以标记为硬件验收完成。
- 邮储完整登录地址、证书认证方式和后续业务自动化不在本变更范围，需要在 Key 检测稳定后单独规划。
