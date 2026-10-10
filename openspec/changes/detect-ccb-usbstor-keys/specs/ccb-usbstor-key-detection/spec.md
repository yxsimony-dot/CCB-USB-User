## Purpose

为通用建行 USB 启动器定义基于当前连接设备、USBSTOR 驱动服务和同一设备关联虚拟光盘 CCB 标记的插入检测能力，使同类 Key 无需逐个登记型号也可识别，并保持设备关联、延迟就绪和身份认证的边界。

## ADDED Requirements

### Requirement: Match connected USBSTOR devices with related CCB media

The application SHALL 在建行检测策略中仅将同时满足以下条件的设备判为匹配：当前已连接的 USB 大容量存储节点使用 USBSTOR 服务；该节点关联虚拟光盘的设备名称或卷标包含 CCB。服务名与 CCB 子串比较 MUST 忽略大小写，不依赖本地化的设备显示名称。

#### Scenario: First and second sampled Keys
- **WHEN** 当前 USBSTOR 设备关联光盘名称为 TENDYRON U-DISK USB Device、卷标为 CCB
- **THEN** 设备被识别为匹配的 Key

#### Scenario: Third and fourth sampled Keys
- **WHEN** 当前 USBSTOR 设备型号为 uKeyCI800-K54 或 uKeyCI800-K156，关联光盘名称包含 HDZB-CCB
- **THEN** 两种型号均被识别为匹配的 Key

#### Scenario: Case insensitive and either marker source
- **WHEN** 当前设备服务名为 usbstor，关联光盘名称或卷标之一包含 cCb，另一字段为空或不包含标记
- **THEN** 设备被识别为匹配的 Key

#### Scenario: Missing one of the required conditions
- **WHEN** USBSTOR 设备没有关联光盘 CCB 标记，或有标记的设备服务不是 USBSTOR，或服务信息缺失
- **THEN** 当前快照不将该设备识别为匹配的 Key

### Requirement: Keep evidence within the same connected device

The application MUST 只使用当前同一 USB 设备下关联虚拟光盘的名称和已确认归属于该光盘的卷标。历史断开记录、无关设备和无法确定归属的卷 SHALL NOT 用于补足匹配条件。

#### Scenario: Unrelated device carries CCB
- **WHEN** USBSTOR 设备 A 无标记，而另一设备 B 或本地磁盘的卷标包含 CCB
- **THEN** A 不匹配

#### Scenario: Previous device shared the same instance identifier
- **WHEN** 上一个 Key 已拔出，新设备沿用同一实例 ID 但当前没有 CCB 标记
- **THEN** 不复用上个 Key 的命中记录，新设备保持未匹配

### Requirement: Avoid individual model restrictions

The application SHALL 识别所有满足上述双条件的设备，不额外要求指定 VID/PID、型号、硬件版本、序列号、实例 ID 值、盘符或 CDFS 文件系统。旧型号白名单 SHALL NOT 绕过新策略的任一必要条件。

#### Scenario: Unlisted compatible model
- **WHEN** 一个未登记 VID/PID 和型号、不同版本与盘符的设备使用 USBSTOR 且关联虚拟光盘名称含 CCB，文件系统不是 CDFS 或尚未读取到
- **THEN** 该设备仍匹配

#### Scenario: Previously allowlisted model lacks marker
- **WHEN** 一个符合旧 VID/PID 和型号规则的 USB 设备不满足新双条件
- **THEN** 建行新策略不将其识别为匹配的 Key

### Requirement: Retry metadata readiness without duplicate business attempts

The application SHALL 在业务启动后的首次检测及设备变化检测中，为元数据尚未满足条件的 USBSTOR 候选进行有限、可取消的重新枚举；默认每隔 1 秒最多追加 5 次。信息就绪后 SHALL 正常匹配，次数耗尽后 SHALL 保持等待而不伪造成功；拔出、退出和新的设备变化 SHALL 使对应旧刷新失效。

#### Scenario: CCB volume appears after initial enumeration
- **WHEN** 第一次只能读取到 USBSTOR，随后重试读取到该设备关联卷标 CCB
- **THEN** 程序识别设备并仅按现有生命周期触发一次业务尝试

#### Scenario: Retry expires or device leaves
- **WHEN** 候选设备一直没有标记、已拔出，或应用在等待期间退出
- **THEN** 重试在上限或取消时停止，不使用过期结果重新触发业务

#### Scenario: Later metadata event after retry limit
- **WHEN** 有限重试已耗尽，随后收到包含已就绪元数据的设备变化通知
- **THEN** 重新评估当前快照并按双条件判断

#### Scenario: Previously matched node temporarily loses metadata
- **WHEN** 已匹配 USB 节点仍然连接，仅关联光盘标记暂时缺失并在有限重试期间恢复
- **THEN** 不把中间元数据状态发布为真实拔插，不增加认证周期、不再次读取或提交 PIN

#### Scenario: Metadata remains invalid after retries on the same node
- **WHEN** 已匹配节点仍连接但重试耗尽仍无法验证 CCB 标记
- **THEN** 当前设备检测为未匹配，停止相关导航并释放认证资源，但保留该物理插入周期的已用认证额度
- **AND** 后续同节点标记恢复可重新显示识别结果，但不自动开启新的 PIN 尝试；真正拔出后重新插入仍按原流程处理

### Requirement: Preserve explicit startup and authentication boundary

The application SHALL 保留手动启动、取消和重复操作保护；新规则只表示插入检测匹配，不代表身份认证成功，不改变 PIN、证书或导出行为。

#### Scenario: Application opened before business starts
- **WHEN** 用户打开 EXE 但未点击启动业务
- **THEN** 不因新检测策略自动开始业务枚举、重试或认证

#### Scenario: Detected Key does not match configured authentication
- **WHEN** 设备符合检测规则，但不满足既有认证条件
- **THEN** 插入检测可匹配，但认证仍按既有规则报告结果，不宣告登录成功或改写 PIN

### Requirement: Explicit configuration migration

The application SHALL 支持显式选择新的建行检测策略，且启用后不再要求旧型号字段参与验证或匹配。缺少策略字段的旧配置 SHALL 保持旧检测行为，未知策略 SHALL 报配置错误；本次发布 SHALL 显式将建行 profile 迁移为新策略并保留其他设置。

#### Scenario: New strategy without model allowlist
- **WHEN** 配置选择建行 USBSTOR 与 CCB 策略且没有旧 VID/PID 型号规则
- **THEN** 检测配置有效并执行双条件规则

#### Scenario: Legacy and invalid strategy handling
- **WHEN** 配置未提供新策略字段或提供未知策略值
- **THEN** 前者保持旧行为，后者明确报错，不静默改用其他策略
