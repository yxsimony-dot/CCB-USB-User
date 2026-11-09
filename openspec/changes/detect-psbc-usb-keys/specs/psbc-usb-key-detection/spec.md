## Purpose

为邮储银行流程提供覆盖五个已知厂商族的 USB Key 检测能力，在不产生设备初始化副作用的前提下报告物理存在、环境就绪和可用状态，并服从用户当前选择的银行流程。

## ADDED Requirements

### Requirement: Detect supported PSBC vendor families
当邮储银行为当前选择的银行时，应用 SHALL 检测飞天、华大智宝、天地融、深圳文鼎创和握奇五个邮储 USB Key 厂商族。检测 SHALL 使用厂商适配器确认当前连接的实体 Token，而不是仅根据系统中已安装的驱动或软件判断 Key 已插入。

#### Scenario: Supported vendor Key is connected
- **WHEN** 用户选择邮储银行并插入上述任一厂商的可用邮储 Key
- **THEN** 应用识别对应厂商族，并报告存在可用于邮储流程的 USB Key

#### Scenario: Current Tendyron sample is connected
- **WHEN** 用户选择邮储银行，当前设备包含 `VID_1780&PID_0401`、`TENDYRON U-DISK` 介质和 `PSBC` 卷标，并且天地融邮储环境可用
- **THEN** 应用将该设备识别为天地融厂商族的邮储 Key

#### Scenario: Vendor software exists without a Token
- **WHEN** 邮储厂商驱动、服务或 CSP 已安装，但没有对应实体 Token 连接
- **THEN** 应用报告 Key 不存在，不得仅凭已安装环境报告可用

### Requirement: Report detection readiness separately from physical presence
邮储检测结果 MUST 区分 `Absent`、`PresentButEnvironmentUnavailable` 和 `Ready`。`Ready` SHALL 表示厂商探测已确认实体 Token 且所需邮储驱动、服务或加密提供程序可用；环境故障不得伪装成设备未插入。

#### Scenario: Key is absent
- **WHEN** 没有任何受支持的邮储实体 Token 连接
- **THEN** 检测状态为 `Absent`

#### Scenario: Key is present but its environment is unavailable
- **WHEN** 已确认受支持厂商的实体 Token 已连接，但对应驱动、服务或邮储加密提供程序缺失或不可用
- **THEN** 检测状态为 `PresentButEnvironmentUnavailable`，并提供不包含敏感信息的诊断原因

#### Scenario: Key and environment are ready
- **WHEN** 厂商探测确认实体 Token 已连接且对应邮储环境可用
- **THEN** 检测状态为 `Ready`，并在厂商接口能够安全提供时附带稳定的 Token 身份

### Requirement: Keep ordinary detection read-only
普通插入检测 SHALL 仅执行读取和查询操作，不得初始化、格式化、解锁 Token，不得提交 PIN，也不得调用可能改变 Token 状态的 `TokenInitialize` 类操作。

#### Scenario: Presence is refreshed repeatedly
- **WHEN** 系统因插拔通知或元数据重试多次刷新同一个邮储 Key
- **THEN** 每次检测仅查询存在性和环境状态，不改变 Token 状态，也不增加 PIN 尝试次数

### Requirement: Route detection through the selected bank only
用户当前选择的银行 SHALL 是检测、认证、网页和业务运行时的唯一活动路由。同一个 Key MAY 同时满足建设银行和邮储银行的识别规则，这种重叠不得被当作错误，也不得自动改变用户选择的银行。

#### Scenario: Overlapping Key while PSBC is selected
- **WHEN** 一个 Key 同时满足建设银行和邮储银行的识别规则，并且用户当前选择邮储银行
- **THEN** 只有邮储检测和邮储后续流程运行，建设银行流程不接收该设备事件

#### Scenario: Overlapping Key while CCB is selected
- **WHEN** 一个 Key 同时满足建设银行和邮储银行的识别规则，并且用户当前选择建设银行
- **THEN** 只有建设银行检测和建设银行后续流程运行，邮储流程不接收该设备事件

#### Scenario: Bank is switched with a Key connected
- **WHEN** 用户在 Key 保持连接时从一个银行切换到另一个银行
- **THEN** 应用先撤销并释放旧银行运行时，再按当前设备快照初始化新银行流程

#### Scenario: Previous bank publishes a late callback
- **WHEN** 银行切换完成后，旧银行监听器产生延迟检测回调
- **THEN** 应用忽略该回调，且当前银行的界面、认证和业务状态不受影响

### Requirement: Keep device evidence and lifecycle current
设备介质、卷标和硬件信息 SHALL 仅在能够确认属于同一物理 USB 设备时组合使用。检测 SHALL 对元数据延迟进行有限且可取消的重试，并在设备真实拔出或银行流程被撤销时终止对应探测。

#### Scenario: Marker belongs to another device
- **WHEN** 一个 USB 设备只有厂商硬件信息，另一个无关设备或本地卷包含 `PSBC` 标记
- **THEN** 应用不得把两个设备的证据拼接成一个邮储 Key

#### Scenario: Metadata becomes available after insertion
- **WHEN** 初次枚举只发现候选 USB 节点，后续有限重试获得同设备介质、服务或 Token 探测结果
- **THEN** 应用更新为最终状态，并且一个物理插入周期只产生一次有效就绪转换

#### Scenario: Device leaves during probing
- **WHEN** 邮储 Key 在厂商探测或元数据重试期间拔出
- **THEN** 旧探测被取消，状态转为 `Absent`，迟到结果不得恢复已拔出的设备
