## Purpose

提供一个最小的 Windows 桌面入口：仅在配置指定的 USB 设备存在时显示内置网页，并以可测试的状态与错误反馈支持后续扩展，而不在首版引入凭据或网页流程自动化。

## ADDED Requirements

### Requirement: Configured USB device gates browser opening
系统 SHALL 在启动时检查已连接设备，并持续监听设备到达事件；只有设备满足当前配置中的全部必填匹配条件时，才允许打开浏览器页面。

#### Scenario: Matching device is already present at startup
- **WHEN** 应用启动时已存在与配置匹配的 USB 设备
- **THEN** 系统进入设备已识别状态并开始打开浏览器页面

#### Scenario: Matching device is inserted after startup
- **WHEN** 应用正在等待且收到与配置匹配的 USB 设备到达事件
- **THEN** 系统进入设备已识别状态并开始打开浏览器页面

#### Scenario: Unrelated USB device is inserted
- **WHEN** 应用收到不满足配置匹配条件的 USB 设备到达事件
- **THEN** 系统保持等待状态且不打开浏览器页面

### Requirement: Current token profile is supported
默认设备配置 MUST 使用当前已观测到的型号级特征 `VID_1677`、`PID_0054` 和总线描述 `uKeyCI800-K156`。系统 MAY 使用 `HDZB-CCB USB AutoRunDisk` 设备名称、`HDZB_USBKEY_KEY` 卷标或 `CDFS` 文件系统作为辅助校验，但 SHALL NOT 将端口位置、容器 ID 或临时 PnP 实例后缀作为持久身份。

#### Scenario: Current token is connected through another USB port
- **WHEN** 当前型号的 Token 从不同 USB 端口接入且仍报告配置中的稳定型号特征
- **THEN** 系统将其识别为匹配设备，不依赖原端口生成的实例后缀

#### Scenario: Another physical token of the same model is connected
- **WHEN** 另一只设备报告与默认配置相同的型号级特征
- **THEN** 第一版将其视为匹配设备，并不声称区分同型号的实体 Key

### Requirement: Matching device opens configured page in embedded browser
检测到匹配设备后，系统 SHALL 显示桌面主窗口，初始化内置浏览器，并导航到配置的 HTTPS 地址。一次设备识别周期 MUST NOT 因重复设备通知而创建多个主窗口或重复导航。

#### Scenario: Browser opens successfully
- **WHEN** 匹配设备已识别、浏览器运行时可用且目标地址有效
- **THEN** 系统显示主窗口并在内置浏览器中导航到该地址

#### Scenario: Duplicate device notifications arrive
- **WHEN** 系统已为当前匹配设备开始或完成打开页面，随后收到重复到达通知
- **THEN** 系统保持同一个主窗口和当前导航，不创建重复实例

#### Scenario: Matching device is removed after the page opens
- **WHEN** 页面已打开后匹配设备被拔出
- **THEN** 第一版保持当前窗口和页面打开，并更新设备状态而不强制关闭浏览器

### Requirement: Operational state and failures are visible
系统 SHALL 向用户显示等待设备、设备已识别、正在打开页面、页面已就绪或错误状态。USB 枚举、浏览器初始化和页面导航失败 MUST 被捕获并显示可诊断消息，而不是导致应用无提示退出。

#### Scenario: Embedded browser runtime is unavailable
- **WHEN** 系统无法初始化内置浏览器运行时
- **THEN** 应用保持运行并显示浏览器初始化失败状态及可诊断信息

#### Scenario: Target navigation fails
- **WHEN** 浏览器无法完成对配置地址的导航
- **THEN** 应用显示导航失败状态，并允许用户在不重启应用的情况下重试

### Requirement: Version one does not handle secrets or automate page input
第一版 MUST NOT 收集、保存、读取或自动填写密码、验证码、动态口令及其他秘密值，也 MUST NOT 自动操作网页表单或原生安全控件。

#### Scenario: Page requests a password or verification value
- **WHEN** 已打开的网页显示密码、验证码或动态口令输入项
- **THEN** 应用不读取任何本机密钥或环境变量、不自动填写该输入项，并将交互留给用户在网页中完成
