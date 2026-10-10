## Purpose

使其他已满足支持范围的 Windows x64 电脑能够通过单一安装程序取得完整应用，明确首次环境初始化、当前 Windows 用户归属与依赖失败状态，同时防止把开发机凭据、缓存和银行业务文件带入分发包。

## ADDED Requirements

### Requirement: Installer only distribution with clean application payload

系统 SHALL 交付一个 Windows x64 安装 EXE，包含主程序及完整运行载荷、通用配置、所需浏览器扩展和本机认证组件包；SHALL 排除 PIN、证书、浏览器数据、银行文件、日志、备份、测试数据和开发机专用路径，不提供便携 ZIP 成品。

#### Scenario: Clean installation on a supported computer
- **WHEN** 用户在支持的 Windows x64 系统运行安装程序
- **THEN** 安装程序安装完整应用及其随包资源，并创建指向最终安装路径的启动入口和卸载入口
- **AND** 无需复制开发机目录或安装 .NET SDK

#### Scenario: Reject non-distributable payload
- **WHEN** 待打包载荷包含浏览器缓存、凭据、证书、业务 PDF 或开发机路径
- **THEN** 产物生成失败并报告问题类别，不产生标记为可交付的安装包

#### Scenario: Unsupported operating system or architecture
- **WHEN** 运行系统或架构不在安装包声明的支持范围
- **THEN** 安装器在修改应用或认证组件前明确拒绝安装

### Requirement: Current user ownership and bounded elevation

系统 SHALL 为发起安装的当前交互 Windows 用户安装应用、创建用户入口并以非提升权限运行主程序；仅所需本机组件/官方依赖安装请求管理员权限。系统 SHALL 明确一期一台电脑仅支持一个指定交互 Windows 用户使用全机认证组件，禁止覆盖另一用户所有的组件注册。

#### Scenario: Same user accepts UAC
- **WHEN** 当前交互管理员用户正常运行安装器并确认本机组件 UAC
- **THEN** 应用和用户入口属于该交互用户，主程序首次运行回到该用户的普通权限
- **AND** 不以提升后的安装进程保存用户 PIN 或建立银行会话

#### Scenario: Existing broker belongs to another user
- **WHEN** 另一 Windows 用户尝试安装或修复而本机认证组件已有不同所有者
- **THEN** 系统停止组件变更并说明冲突，原用户组件、PIN 和设置保持完整

#### Scenario: Different administrator credentials or no interactive user
- **WHEN** 安装提升使用另一 Windows 账号，或者无法证明发起安装的交互用户
- **THEN** 系统明确说明该权限模式尚未受支持，不报告认证环境就绪，也不将组件或凭据绑定到错误用户

### Requirement: Official dependencies and resumable first use readiness

系统 SHALL 在首次使用时分别检查浏览器运行时、建行官方驱动/原生通信组件、本产品认证组件和本机 PIN；SHALL 通过经核实的官方来源获取缺失第三方依赖，验证下载内容，并在需要时请求管理员确认。应用安装完成与业务环境就绪 SHALL 是不同状态，USB Key 可检测 SHALL NOT 单独证明登录依赖已经就绪。

#### Scenario: Missing official dependency
- **WHEN** 目标机缺少浏览器运行时或银行原生通信组件
- **THEN** 系统显示缺失项，并提供对应官方安装流程
- **AND** 只有重新检测成功后才将该项标记为就绪

#### Scenario: Dependency requires manual vendor workflow
- **WHEN** 官方依赖无法通过已验证的自动安装参数安全安装
- **THEN** 系统打开经核实的官方安装入口，说明待完成项并允许安装完成后重新检测
- **AND** 不以“应用已安装”宣称业务已可直接使用

#### Scenario: Download cancelled invalid or restart required
- **WHEN** 下载/来源校验失败、用户取消安装或官方组件要求重启
- **THEN** 系统显示真实状态和恢复操作，不启动银行业务，不静默使用未验证依赖

#### Scenario: Existing environment is ready
- **WHEN** 依赖检测及初始化能力检查均通过
- **THEN** 系统复用已有环境，不重复安装或卸载共享的第三方组件

### Requirement: Local first PIN and user data isolation

系统 SHALL 在新电脑或新用户首次使用时要求本地设置 PIN，不从安装包、原机器或其他用户导入凭据、证书及登录状态。默认下载目录 SHALL 属于当前用户，用户可在界面选择其他可写目录。

#### Scenario: First run has no saved PIN
- **WHEN** 应用已安装且本机环境就绪，但当前用户没有已保存 PIN
- **THEN** 界面提示该用户设置 PIN，保存到其本机凭据存储，不将 PIN 写入安装日志、配置或命令行

### Requirement: Uninstall preserves business data by default

普通卸载 SHALL 删除该用户应用及其拥有的本产品认证组件/任务，默认保留本机 PIN、下载目录设置和已下载文件；SHALL NOT 删除银行驱动、浏览器运行时或其他用户组件。可选清理用户配置 SHALL 显式选择且不删除业务文件。

#### Scenario: Normal uninstall
- **WHEN** 用户执行默认卸载
- **THEN** 应用入口、应用载荷与其拥有的认证组件注册被移除，PIN、下载目录设置和已有 PDF 保留

#### Scenario: Explicit local configuration cleanup
- **WHEN** 用户明确选择清理本产品当前用户配置
- **THEN** 系统仅清理该用户本产品凭据和设置，不递归删除下载目录或其他用户数据
