## Purpose

建立从 Git 源码生成安装程序的可追溯构建契约，让产品版本、主程序与认证组件版本以及安装产物保持一致，并提供未来 GitHub Releases 可消费的机器可读元数据，而不在本期引入自动更新或发布行为。

## ADDED Requirements

### Requirement: One version source for all release components

产品 SHALL 有唯一受版本控制的产品版本源和稳定产品/安装标识，所有应用组件、安装包、界面版本及发布元数据 SHALL 从该来源派生一致版本。源码提交标识 SHALL 可追溯；存在发行 tag 时 SHALL 校验其与产品版本一致。

#### Scenario: Build a declared version
- **WHEN** 从指定提交构建一个声明版本
- **THEN** 主程序、认证组件、安装器和清单版本一致，元数据记录该提交，不依赖开发机旧产物版本

#### Scenario: Tag and declared version disagree
- **WHEN** 发行 tag 与统一版本源不一致
- **THEN** 发行构建失败且不生成标记为有效发行版的安装包

### Requirement: Build from clean source with pinned prerequisites

仓库 SHALL 提供已文档化的单一构建入口与固定工具/依赖版本，在符合构建环境要求的干净检出上完成依赖恢复、测试、干净发布、载荷检查和安装包生成，不依赖开发者现有发布目录、银行会话或本机 PIN。可重复构建 SHALL 指可重建对应功能与版本，不将签名时间戳不同的产物宣称比特完全相同。

#### Scenario: Fresh checkout on another build computer
- **WHEN** 开发者获取源码并按文档准备声明的构建工具后执行构建入口
- **THEN** 构建可生成完整安装 EXE 及附属元数据，不需复制原开发机的 publish、缓存或配置秘密

#### Scenario: Missing prerequisite or failed verification
- **WHEN** 必需工具缺失、依赖锁不一致、测试失败或载荷检查失败
- **THEN** 构建清楚失败，不继续发布部分安装包或复用上次成功产物冒充本次结果

### Requirement: Installer checksum and versioned release manifest

每次有效构建 SHALL 输出安装 EXE、SHA-256 校验文件及具有 schemaVersion 的发布清单。清单 SHALL 包含稳定 productId、version、channel、platform、architecture、minimumOsVersion、sourceRepository、sourceCommit、installerFileName、size、sha256、brokerProtocolVersion、settingsSchemaVersion。产品源码仓库 SHALL 指向 `https://github.com/yxsimony-dot/CCB-USB-User`；清单 SHALL 不含访问 token、个人路径或用户业务信息。

#### Scenario: Verify installer metadata
- **WHEN** 消费者按清单读取安装文件
- **THEN** 文件名、字节数及 SHA-256 与实际产物一致，版本和源提交可用于追溯

#### Scenario: Release download URL does not exist yet
- **WHEN** 本地构建尚未创建正式 GitHub Release
- **THEN** 清单不伪造已存在的 downloadUrl 或 releaseNotesUrl，仍能提供安装文件本地校验与来源信息

### Requirement: Reserve future release integration without automatic updates

本期 SHALL 只定义未来 GitHub Releases 分发接口，不实现后台版本检查、自动下载/替换、自动创建 Release 或自动上传二进制。未来更新安装 SHALL 复用本期稳定产品身份和覆盖升级契约。安装包签名状态 SHALL 如实呈现，SHA-256 SHALL NOT 被描述为已验证发行者身份。

#### Scenario: Launch installed application
- **WHEN** 用户启动本期安装版应用
- **THEN** 应用不因版本检查访问 GitHub，不自动下载新包，用户正常业务不需要 GitHub 凭据

#### Scenario: Build without a signing certificate
- **WHEN** 构建环境未配置可用的产品代码签名证书
- **THEN** 产物与交付说明如实标记未签名状态，不宣称可免除系统信誉提示
