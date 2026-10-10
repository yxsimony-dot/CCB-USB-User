## Purpose

提供不打断银行业务的应用内更新检查与下载能力，使用户能够理解当前版本、可用新版、更新内容及下载结果。通过固定发布来源和文件验证，在网络失败或发布文件异常时保持现有应用可用，并避免误执行不完整安装包。

## ADDED Requirements

### Requirement: Manual and startup update discovery

应用 SHALL 提供“检查更新”入口，并在每次启动后后台检查一次；自动检查 MUST NOT 阻塞初始化、自动下载或自动启动安装程序。公开仓库更新查询 MUST NOT 要求用户配置 GitHub 账号或访问 token。

#### Scenario: Manual check finds a newer release
- **WHEN** 用户手动检查且固定仓库存在更高的有效兼容正式版本
- **THEN** 展示当前版本、新版本和更新说明，并让用户决定是否下载

#### Scenario: Startup check encounters no release or a network failure
- **WHEN** 启动检查发现仓库尚无 Release、断网或被限流
- **THEN** 当前应用继续可用，分别记录暂无版本或检查失败状态，允许用户重试

### Requirement: Numeric compatible release selection

应用 SHALL 从正式稳定版本中选择最高有效兼容的数字版本，仅对高于本地的版本提供升级。应用 MUST 验证产品、版本、来源、架构、支持的清单及资产对应关系，忽略草稿和预发布，不以发布时间或字符串排序决定新版。

#### Scenario: Semantic numeric ordering
- **WHEN** 仓库包含 1.1.9、1.1.10 和较晚发布的 1.1.8
- **THEN** 对低版本客户端推荐 1.1.10，已装 1.1.10 的客户端不被降级

#### Scenario: Candidate is incompatible or incomplete
- **WHEN** 较高候选的清单不受支持、架构不同或发布资产异常
- **THEN** 不执行该资产并显示原因，不误报检查成功且已是最新；若存在其他有效升级候选则清楚区分

#### Scenario: Pagination cannot complete
- **WHEN** 未获取足够分页以确认最高有效版本便超时或触发响应上限
- **THEN** 报告检查未完成，不把部分结果描述为完整最新结论

### Requirement: Verified cancellable installer downloads

应用 SHALL 在用户选择后下载对应 Release 的完整安装包，并在允许安装之前验证实际大小和 SHA-256。下载 SHALL 支持取消、进度和错误重试，只接受可信来源及有效 HTTPS 重定向，缓存不得落入应用安装目录或银行下载目录。

#### Scenario: Valid completed download
- **WHEN** 对应安装包完整下载并通过验证
- **THEN** 显示可安装状态，标明版本及真实签名状态

#### Scenario: Cancelled or corrupt download
- **WHEN** 用户取消、连接超时、超过文件限制或校验不符
- **THEN** 不执行文件，清理该次不完整下载并保留当前应用与用户数据

### Requirement: Independent bounded update activity

应用 SHALL 对更新请求设置超时和取消，合并重复检查，处理限流，关闭后不回写过期 UI。更新说明 MUST 按非执行性内容展示，文件哈希 MUST NOT 被宣称为发布者签名认证。

#### Scenario: Repeated clicks and application close
- **WHEN** 用户重复检查或在请求过程中关闭应用
- **THEN** 不堆积并行重复操作，取消相关请求，不阻塞正常退出或更新已销毁窗口
