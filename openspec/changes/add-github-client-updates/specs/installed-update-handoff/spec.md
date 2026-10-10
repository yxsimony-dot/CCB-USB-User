## Purpose

把客户端已验证的新安装包交给既有安装升级机制执行，保持主程序和认证组件版本一致。业务活动必须先正常结束，升级不得删除 PIN、下载设置或银行 PDF，并应真实区分安装器已启动与新版本已经安装完成。

## ADDED Requirements

### Requirement: Installation only from an idle business session

应用 SHALL 在安装前再次确认业务为空闲且无导出、停止或关闭操作，并防止新业务与安装交接竞态。检查及下载 MAY 在业务进行中执行，但安装 MUST NOT 强制中断正在进行的业务。

#### Scenario: User requests installation while logged in
- **WHEN** 页面已登录且业务仍处于活动状态，即使没有耗时按钮任务
- **THEN** 提示先中止业务，不执行安装器；返回空闲后可以重试

#### Scenario: Business starts during update handoff
- **WHEN** 更新安装已通过空闲校验并进入交接阶段
- **THEN** 新业务启动暂时不可用，直至安装交接失败恢复或应用正常退出

### Requirement: Reuse the complete interactive installer

应用 SHALL 执行已验证的整包安装器并正常退出，沿用原 Windows 用户和既有 UAC、版本校验、认证组件绑定与恢复策略。不得仅覆盖主 EXE 或宣称静默热更新。

#### Scenario: Installer launch succeeds
- **WHEN** 空闲用户选择安装且安装器成功启动
- **THEN** 应用进入正常退出流程，后续显示交互式安装界面，不提前声称升级成功

#### Scenario: Launch fails or the user cancels installation
- **WHEN** 安装器无法启动或用户取消安装
- **THEN** 显示可理解的状态；未退出应用可恢复正常操作，已退出时用户可从原入口重开并重试

#### Scenario: A cached installer changes before execution
- **WHEN** 缓存文件在下载验证后发生变化
- **THEN** 执行前复核失败，禁止启动该文件并要求重新下载

### Requirement: Preserve existing user state and recover failed upgrades

更新 SHALL 沿用固定产品身份与目录，保留当前用户 PIN、下载目录和既有 PDF。失败 SHALL 交由既有安装事务恢复，恢复失败时保留诊断与备份，不启动半更新业务。

#### Scenario: Successful same-user upgrade
- **WHEN** 用户完成新版安装后打开应用
- **THEN** 显示新版版本，保留原设置和文件，等待用户主动启动银行业务

### Requirement: Bootstrap legacy installations explicitly

交付说明 SHALL 说明无更新入口的 1.0.0 需要先手动安装一次支持更新的版本，之后再使用应用内更新。开发目录运行的用户 SHALL 被告知实际安装目标为固定安装目录。

#### Scenario: Existing 1.0.0 user obtains the first updater release
- **WHEN** 用户仍在使用 1.0.0
- **THEN** 提供可手动安装的完整新版及说明，不声称旧程序已具备远程更新入口
