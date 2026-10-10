## Purpose

定义已安装应用跨版本更新时的稳定产品身份、用户数据保留、认证组件重新绑定和失败恢复行为，使用户将来从相同发布源下载新版安装包后可以继续使用已有配置，而不会进入主程序与认证组件版本混用的状态。

## ADDED Requirements

### Requirement: Stable product identity and installation location

同一产品各版本 SHALL 使用稳定安装身份及当前用户固定应用目录，升级 SHALL 复用既有入口和卸载项。系统 SHALL 支持同版本修复及更高稳定版本覆盖升级，并默认拒绝降级，不静默创建第二个版本安装实例。

#### Scenario: Install a newer version
- **WHEN** 同一用户运行同产品更高版本安装包
- **THEN** 安装器识别为覆盖升级，沿用已有目录及产品注册，成功后用户入口启动新版

#### Scenario: Repair current version
- **WHEN** 同一用户运行当前相同版本安装包并选择修复
- **THEN** 应用载荷和本机认证绑定可被修复，不重置用户配置

#### Scenario: Attempt downgrade
- **WHEN** 安装包版本低于已安装版本
- **THEN** 安装器在替换应用前拒绝并说明不支持降级，既有版本继续可用

### Requirement: Preserve local credentials settings and downloads

覆盖升级 SHALL 保留当前用户已保存 PIN、下载目录及已完成业务文件，并保持凭据命名兼容。用户设置未来发生格式变化时 SHALL 有明确版本及可恢复迁移；安装包默认值 SHALL NOT 无提示覆盖有效用户设置。

#### Scenario: Upgrade configured installation
- **WHEN** 已设置 PIN 和下载目录的用户完成升级
- **THEN** 新版继续显示并使用原 PIN 与下载目录，无需重新设置，已有文件内容及名称不变

#### Scenario: Saved directory is unavailable
- **WHEN** 升级后原下载目录已不存在或不可写
- **THEN** 应用提示用户重新选择，不静默将下载改到安装目录或另一用户目录

### Requirement: Complete application and broker upgrade validation

升级 SHALL 正常结束本产品业务会话、等待应用释放文件，然后部署完整新载荷并从最终应用位置重新绑定本机认证组件。只有新版主程序以原用户普通权限完成组件身份/协议健康验证后 SHALL 报告升级成功；该验证 SHALL 不执行银行登录、PIN 提交或导出。

#### Scenario: Healthy version upgrade
- **WHEN** 新版应用和组件全部安装且普通用户健康验证通过
- **THEN** 安装器提交新版本状态并允许正常启动业务，清理不再需要的技术备份

#### Scenario: Business cannot stop within bounded time
- **WHEN** 旧版应用未能在规定期限退出或文件仍被占用
- **THEN** 安装器停止替换并提示关闭，不继续混合部署，不结束其他银行应用

#### Scenario: Component installation exits successfully but new binding fails
- **WHEN** 本机组件安装命令成功而新版主程序健康验证失败
- **THEN** 安装器报告升级失败并进入恢复流程，不以组件启动成功代替升级成功

### Requirement: Restore complete previous installation after upgrade failure

在新版本提交前，安装器 SHALL 保留恢复旧应用、版本注册及认证组件/任务绑定所需的技术备份；捕获到替换、组件注册或健康验证失败时 SHALL 恢复完整旧安装并验证恢复结果。备份 SHALL 不包含 PIN 或业务文件。恢复失败或升级中断 SHALL 有明确的待恢复状态，禁止无提示启动混合版本。

#### Scenario: New version fails before commit
- **WHEN** 新版部署或验证失败且旧安装可恢复
- **THEN** 旧主程序、旧组件及对应绑定恢复并通过验证，用户数据保持完整

#### Scenario: Recovery cannot complete
- **WHEN** 文件占用或系统错误导致旧版本恢复失败
- **THEN** 保留技术备份和修复记录，显示需修复状态，不宣称旧版已恢复或新版已就绪

#### Scenario: Interrupted transaction is found next time
- **WHEN** 用户再次运行安装器且发现未提交的上次升级事务
- **THEN** 安装器先提供恢复/修复流程，不忽略未完成事务开始另一次覆盖

### Requirement: Upgrade cleanup does not perform user uninstall

覆盖升级内部的旧文件清理 SHALL NOT 删除保留凭据/设置、卸载已更新认证组件或卸载第三方共享依赖。

#### Scenario: Remove obsolete application payload
- **WHEN** 新版成功后清理旧版载荷
- **THEN** 只清理安装清单拥有的旧文件，保留用户数据、新认证绑定及共享第三方环境
