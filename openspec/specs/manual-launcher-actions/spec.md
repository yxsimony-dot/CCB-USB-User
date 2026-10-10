# manual-launcher-actions Specification

## Purpose
为现有 USB 网银启动器提供独立的环境安装、PIN 设置与业务启动入口，让用户明确控制业务开始时机。此能力仅调整操作入口与触发关系，保留既有设备识别、客户端证书认证、凭据保护及原生密码窗口处理行为。

## Requirements

### Requirement: Three independent primary actions

The application SHALL 在网页上方提供“安装环境”“设置 PIN”“启动业务”三个主要入口，保留原有维护操作作为相应入口的二级菜单，并在原有窗口中展示网页。

#### Scenario: Main actions and maintenance actions are reachable
- **WHEN** 用户打开应用
- **THEN** 上方可见三个主要入口
- **AND** 安装环境入口提供现有安装/更新与卸载操作，设置 PIN 入口提供设置/更换与删除操作
- **AND** 操作禁用时有可理解的状态提示，最小支持窗口尺寸下入口仍可访问

### Requirement: Startup remains idle until business is requested

The application SHALL 在启动时仅初始化界面、配置和操作依赖，显示“业务未启动”；在首次明确启动业务之前不得进行业务 USB 监听/枚举、网页初始化/导航、凭据读取用于解锁或认证。

#### Scenario: Launch with a connected Key and saved credentials
- **WHEN** U 盾和本机凭据已经就绪，用户打开 EXE 但未点击启动业务
- **THEN** 不会自动进入网银或出现由业务触发的密码窗口
- **AND** 状态提示用户点击启动业务，不把尚未检测的设备标为已连接或已断开

#### Scenario: Initialization fails before business begins
- **WHEN** 配置或应用初始化失败后用户重试初始化
- **THEN** 初始化恢复后仍停留在业务未启动状态
- **AND** 初始化重试本身不会自动开始业务

### Requirement: Environment preparation does not start business

The application SHALL 通过安装环境入口调用现有本机认证组件安装/更新或卸载能力；完成、失败或取消此操作均不得自动启动或恢复业务。本变更不增加驱动或 WebView2 的自动安装能力。

#### Scenario: Install environment successfully
- **WHEN** 用户完成现有认证组件安装
- **THEN** 环境状态更新
- **AND** 不自动检测 U 盾用于启动、不打开网银、不发起认证

### Requirement: PIN preparation remains independent

The application SHALL 允许在业务未启动时设置 PIN，沿用原凭据存储和待验证机制。设置入口不得自动安装组件，也不得在保存或更换完成后自动解锁、导航或提交厂商窗口；保存成功不得显示为认证成功。

#### Scenario: Environment is unavailable
- **WHEN** 用户点击设置 PIN，而现有认证组件前置检查未通过
- **THEN** 显示通过安装环境入口处理的提示
- **AND** 不隐式执行安装或业务操作

#### Scenario: Save PIN before starting business
- **WHEN** 用户在业务未启动时成功保存 PIN
- **THEN** 显示凭据已保存、等待用户启动业务
- **AND** 不读取该凭据用于解锁，不导航或生成认证成功标记

#### Scenario: Change PIN after business has previously started
- **WHEN** 用户在允许维护的非忙状态更换 PIN
- **THEN** 保存完成及随后设备通知不会自动使用新凭据进行业务
- **AND** 只有用户再次明确启动业务才应用待处理的凭据变更
- **AND** 保持原有页面与认证生命周期语义，不额外强制刷新或自动重新登录

#### Scenario: Delete a saved but unapplied PIN
- **WHEN** 用户保存 PIN 后尚未启动业务，又删除本机 PIN
- **THEN** 原有凭据及验证标记删除生效
- **AND** 后续启动不会重放先前保存事件或使用已删除的凭据

### Requirement: Explicit business action reuses the existing flow

The application SHALL 仅在用户明确启动时进入或继续原有业务流程。启动后使用原设备匹配、认证准备、浏览器导航、证书选择、密码窗口续接和错误处理顺序。

#### Scenario: Start with Key already inserted
- **WHEN** 准备项就绪且 U 盾已插入，用户点击启动业务
- **THEN** 检测现有设备并进入既有认证和页面流程

#### Scenario: Start before inserting Key
- **WHEN** 用户点击启动业务而 U 盾尚未插入
- **THEN** 显示等待 U 盾并监听设备
- **AND** 后续匹配设备插入后按原流程继续

#### Scenario: Prerequisite is missing during a business attempt
- **WHEN** 业务发现缺少环境或 PIN
- **THEN** 提示使用相应准备入口
- **AND** 准备完成后仍需用户明确点击业务入口继续
- **AND** 不绕过现有认证前置条件

#### Scenario: Device is removed and reinserted during normal business
- **WHEN** 业务已启动且没有等待用户应用的准备变更，用户拔出并重新插入 U 盾
- **THEN** 按现有取消、资源清理及新插入周期规则执行
- **AND** 不额外要求每次插入都重新点击业务按钮

### Requirement: Entry separation preserves lifecycle protections

The application SHALL 防止重复或冲突操作，保留原来的单次尝试、窗口和证书匹配、取消及退出清理规则；普通业务按钮点击不得制造新的插入周期或重置认证尝试额度。

#### Scenario: Repeated business clicks
- **WHEN** 用户连续点击业务入口
- **THEN** 只启动一套设备监听和业务流程，不重复提交 PIN

#### Scenario: Authentication is busy
- **WHEN** 原有认证或密码窗口操作正在进行
- **THEN** 不允许并发安装/卸载或修改/删除 PIN，也不发起第二次业务操作

#### Scenario: Retry business failure
- **WHEN** 业务已启动并进入现有可重试错误状态，用户明确重试
- **THEN** 沿用原错误重试规则
- **AND** 不通过重建运行时或伪造拔插重置单次尝试保护

#### Scenario: Close while idle or waiting
- **WHEN** 用户在未启动或等待 U 盾时关闭窗口
- **THEN** 应用按原有生命周期释放已创建资源并正常退出
