## MODIFIED Requirements

### Requirement: Three independent primary actions

The application SHALL 在网页上方提供“安装环境”“设置 PIN”和业务状态按钮三个主要入口，保留原有维护操作作为相应入口的二级菜单，并在原有窗口中展示网页。业务状态按钮在未启动时显示“启动业务”，会话启动后显示“中止业务”，中止时显示“正在中止…”。

#### Scenario: Main actions and maintenance actions are reachable
- **WHEN** 用户打开应用
- **THEN** 上方可见三个主要入口，业务入口显示启动业务
- **AND** 安装环境入口提供现有安装/更新与卸载操作，设置 PIN 入口提供设置/更换与删除操作
- **AND** 操作禁用时有可理解的状态提示，最小支持窗口尺寸下入口仍可访问

#### Scenario: Session is active or stopping
- **WHEN** 用户已启动一轮业务，包含等待设备、认证、已登录及导出阶段
- **THEN** 业务入口显示可用的中止业务
- **AND** 仅在中止或退出已受理后禁止重复中止操作

### Requirement: Explicit business action reuses the existing flow

The application SHALL 仅在用户明确启动时进入业务流程。每次成功中止后的明确启动建立新会话，使用原设备匹配、认证准备、浏览器导航、证书选择、密码窗口续接和错误处理顺序。在启动前缺少前置条件时保留启动入口；已建立会话后需要准备变更时先通过中止退出本轮，再准备并启动新轮。

#### Scenario: Start with Key already inserted
- **WHEN** 准备项就绪且 U 盾已插入，用户点击启动业务
- **THEN** 检测现有设备并进入既有认证和页面流程

#### Scenario: Start before inserting Key
- **WHEN** 用户点击启动业务而 U 盾尚未插入
- **THEN** 显示等待 U 盾并监听设备，业务按钮允许中止
- **AND** 后续匹配设备插入后按原流程继续

#### Scenario: Prerequisite is missing during a business attempt
- **WHEN** 业务发现缺少环境或 PIN
- **THEN** 提示使用相应准备入口；已建立会话时可先中止回到初始状态
- **AND** 准备完成后仍需用户明确点击业务入口启动
- **AND** 不绕过现有认证前置条件

#### Scenario: Device is removed and reinserted during normal business
- **WHEN** 业务已识别本轮 U 盾，用户拔出该设备（包含同次设备快照中换成另一只 U 盾）
- **THEN** 自动撤销并中止整轮业务，关闭网页及下载窗口，恢复初始空白界面和“启动业务”
- **AND** 再次插入不自动继续；用户点击“启动业务”才建立新会话并从头执行认证和登录
- **AND** 从未识别过 Key 的首次等待，以及同一物理设备仍存在时的临时元数据缺失，不误当作拔出

### Requirement: PIN preparation remains independent

The application SHALL 允许在业务未启动时设置 PIN，沿用原凭据存储和待验证机制。设置入口不得自动安装组件，也不得在保存或更换完成后自动解锁、导航或提交厂商窗口；保存成功不得显示为认证成功。活动业务中先中止再更换 PIN，避免旧会话继续使用维护前的状态。

#### Scenario: Environment is unavailable
- **WHEN** 用户点击设置 PIN，而现有认证组件前置检查未通过
- **THEN** 显示通过安装环境入口处理的提示
- **AND** 不隐式执行业务或安装操作

#### Scenario: Save PIN before starting business
- **WHEN** 用户在业务未启动时成功保存 PIN
- **THEN** 显示凭据已保存、等待用户启动业务
- **AND** 不读取该凭据用于解锁，不导航或生成认证成功标记

#### Scenario: Change PIN after business has previously started
- **WHEN** 用户已中止先前业务并成功更换 PIN
- **THEN** 保存完成及随后设备通知不会自动使用新凭据进行业务
- **AND** 只有用户再次明确启动业务才应用待处理的凭据变更

#### Scenario: Delete a saved but unapplied PIN
- **WHEN** 用户保存 PIN 后尚未启动业务，又删除本机 PIN
- **THEN** 原有凭据及验证标记删除生效
- **AND** 后续启动不会重放先前保存事件或使用已删除的凭据

### Requirement: Entry separation preserves lifecycle protections

The application SHALL 防止重复启动或冲突操作，保留会话内单次尝试、窗口和证书匹配保护。普通重试不得伪造插拔或重置同轮认证额度；明确中止后再启动作为新的用户授权会话处理，不修改设备或银行的错误计数。中止与关闭不受认证、导出忙状态阻挡。

#### Scenario: Repeated business clicks
- **WHEN** 用户快速重复启动或中止操作
- **THEN** 不创建多套设备监听、并行会话或重复 PIN 提交；同一中止请求幂等处理

#### Scenario: Authentication is busy
- **WHEN** 原有认证或密码窗口操作正在进行
- **THEN** 不允许并发安装/卸载或修改/删除 PIN，也不发起第二轮业务
- **AND** 允许中止本轮或关闭程序

#### Scenario: Retry business failure
- **WHEN** 用户在同一会话内使用已有错误重试入口
- **THEN** 沿用原错误重试规则，不通过重建运行时或伪造拔插重置本轮保护
- **AND** 显式中止后重新启动按新会话规则执行

#### Scenario: Close while idle or waiting
- **WHEN** 用户在未启动或等待 U 盾时关闭窗口
- **THEN** 释放已创建资源，并受业务生命周期规定的退出总期限约束
