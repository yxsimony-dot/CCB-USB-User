## Purpose

定义建设银行 USB Key 检测在多银行应用中的非排他语义，使现有建设银行硬件族和介质规则可以继续使用，同时确保检测结果只驱动用户当前选择的建设银行流程。

## ADDED Requirements

### Requirement: Preserve configured CCB matching behavior
当建设银行为当前选择的银行时，应用 SHALL 按建设银行检测策略评估当前连接设备。已知建设银行兼容硬件族或满足建设银行 USBSTOR 介质规则的设备均可被该策略识别。

#### Scenario: Known compatible hardware family is connected
- **WHEN** 用户选择建设银行并插入建设银行检测目录中的兼容硬件族
- **THEN** 建设银行检测可以报告 Key 已连接，即使同一硬件族也被其他银行使用

#### Scenario: CCB media evidence is present
- **WHEN** 用户选择建设银行，当前 USBSTOR 设备的同一物理设备介质名称或卷标包含建设银行标记
- **THEN** 建设银行检测报告 Key 已连接

#### Scenario: Media marker belongs to another device
- **WHEN** 当前候选 USBSTOR 设备没有建设银行介质标记，而无关设备或本地卷包含该标记
- **THEN** 介质匹配分支不得跨设备拼接证据

### Requirement: Treat CCB recognition as non-exclusive
建设银行检测结果 SHALL 只表示当前设备满足建设银行流程的进入条件，不得声明设备只属于建设银行，也不得阻止邮储或其他银行在其自身被选择时识别同一设备。

#### Scenario: Hardware identifier is shared by CCB and PSBC
- **WHEN** `VID_1780&PID_0401` 或其他共享硬件族同时满足建设银行和邮储银行规则
- **THEN** 两个检测器都可以在各自独立评估时返回匹配，不产生银行归属冲突错误

### Requirement: Keep inactive CCB flow isolated
当建设银行不是当前选择的银行时，应用 MUST NOT 保持活动的建设银行设备监听、认证运行时、网页会话或业务命令绑定。

#### Scenario: PSBC is selected while a CCB-compatible Key is connected
- **WHEN** 用户当前选择邮储银行，并连接一个也满足建设银行规则的 Key
- **THEN** 建设银行流程不处理该 Key，不导航建设银行页面，也不执行建设银行认证

#### Scenario: Switch away from CCB
- **WHEN** 用户从建设银行切换到邮储银行
- **THEN** 应用撤销建设银行会话、释放其监听器和运行时，并忽略之后到达的建设银行回调
