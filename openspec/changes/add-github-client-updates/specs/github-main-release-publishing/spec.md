## Purpose

将 main 的每次推送转化为经过测试、可追溯的完整安装版本，让其他电脑上的客户端可以获得更新。发布行为需要区分构建失败、文件上传未完成、正式发布及重复运行，避免提供半成品或覆盖既有版本。

## ADDED Requirements

### Requirement: Publish each validated main push

系统 SHALL 对目标仓库 main 的每次 push 构建该次推送最终提交；所有必需测试、载荷校验和安装编译通过后才发布正式版本，不要求人工指定补丁号。非 main 或不可信来源 MUST NOT 获得自动发布权限。

#### Scenario: Successful main push
- **WHEN** main 更新且完整构建验证通过
- **THEN** 自动发布绑定该提交的完整安装 EXE、发布清单和 SHA-256 校验文件

#### Scenario: Failed validation
- **WHEN** 测试、依赖恢复、载荷检查或安装编译失败
- **THEN** 不公开该次新版，保留失败诊断，已发布版本仍可获取

### Requirement: Automatic comparable and traceable versions

系统 SHALL 自动产生满足 Windows 版本范围的稳定三段数字版本，同一次任务重跑版本不变，后续正常新任务版本递增。程序集、UI、安装器与清单 MUST 使用同一有效版本，源码标识 MUST 指向实际检出的提交，不通过机器人回写触发循环发布。

#### Scenario: Rebuild the same workflow run
- **WHEN** 重跑先前任务
- **THEN** 使用相同版本和源提交，不生成另一版本；允许构建失败留下版本空号

#### Scenario: Version mapping reset or overflow
- **WHEN** 自动版本映射发生计数重置冲突、非正常回退或超出支持范围
- **THEN** 构建清楚失败，不覆盖既有版本或伪造版本来源

### Requirement: Publish only complete immutable assets

系统 SHALL 在所有远端资产验证完成后公开版本。相同发布身份的未完成草稿 SHALL 可恢复；已公开版本 MUST NOT 被后续重跑覆盖。

#### Scenario: Upload interruption
- **WHEN** 上传中断或资产核对失败
- **THEN** 版本保持草稿，不对客户端推荐，后续相同任务可以恢复

#### Scenario: Published version rerun
- **WHEN** 相同任务的正式版本已存在且远端资产完整
- **THEN** 验证提交、版本、产品和任务身份后返回幂等成功，不替换已公开文件

#### Scenario: Conflicting release identity
- **WHEN** 同版本或 tag 已关联另一个提交或发布身份
- **THEN** 停止发布并报告冲突，不覆盖原 Release

### Requirement: Out-of-order builds cannot regress clients

系统 SHALL 允许不同推送独立完成，版本与原始任务对应；晚完成的旧任务 MUST NOT 让客户端把较低版本判断为较新的更新。

#### Scenario: Older build completes last
- **WHEN** 较新推送已发布，旧推送随后也通过验证
- **THEN** 旧任务可发布自己的版本，客户端仍以有效数字版本判断更新且不降级
