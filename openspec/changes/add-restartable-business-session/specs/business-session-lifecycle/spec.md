## Purpose

允许用户明确中止当前网银业务，释放页面和本轮自动操作，保留本机设置后换 Key 开始新一轮。为中止、重复启动及关闭程序定义可验证的隔离和时间上限，避免旧页面、迟到回调或未完成清理阻塞用户操作。

## ADDED Requirements

### Requirement: Business session can be stopped while active

系统 SHALL 在业务会话启动后提供“中止业务”，无论正在等待 Key、登录、查询、下载、显示结果或发生业务错误。受理中止后 SHALL 在一秒内显示“正在中止…”，禁止新增业务操作，并撤销本轮尚未执行的自动输入、提交和后续导出阶段。

#### Scenario: Stop during login
- **WHEN** 登录或原生 PIN 处理正在进行，用户点击中止业务
- **THEN** 中止不受普通忙状态禁用限制
- **AND** 不继续发起新的自动输入或确认；已经提交的银行请求不会被重发，也不被宣称已在银行端撤回

#### Scenario: Stop during combined export
- **WHEN** 流水下载尚未结束，用户中止业务
- **THEN** 取消本轮尚未完成的导出，不进入回单阶段
- **AND** 不把未完成文件发布为成功 PDF

#### Scenario: Physical Key removal ends the whole session
- **WHEN** 本轮已识别的物理 Key 从成功设备快照中消失
- **THEN** 在旧协调器重新认证或处理替换 Key 前撤销会话，触发与手动中止相同的整轮清理
- **AND** 清空页面、公司和继续业务提示，保留 PIN、目录、月份和成功文件
- **AND** 快速拔插或换 Key 不恢复旧轮，下一轮必须由用户明确启动

### Requirement: Successful stop returns to idle with preserved settings

系统 SHALL 在成功中止后关闭所有本轮业务页面和下载窗口，清空当前公司、登录提示及导出进度，恢复初始空白界面和“启动业务”按钮。系统 MUST 保留已保存 PIN、下载目录、所选月份、已安装环境和已完成下载。中止清理总预算 SHALL 为八秒；超过期限且无法确认旧会话已失效时 SHALL 显示可理解的中止失败提示，保留可用的关闭入口，不允许并行启动新会话。

#### Scenario: Stop after successful export
- **WHEN** 两份 PDF 已成功保存后用户中止业务
- **THEN** 页面和公司信息清空，按钮恢复启动业务
- **AND** 已完成文件、本机 PIN、目录和月份不变

#### Scenario: Key remains connected after stop
- **WHEN** 中止完成后 Key 仍插着或用户换插另一只 Key
- **THEN** 保持初始空白状态，不自动导航、认证或输入 PIN
- **AND** 只有再次明确启动才开始检测当前设备并进入业务

#### Scenario: Cleanup does not return
- **WHEN** 中止清理在八秒内未结束且旧会话尚未安全隔离
- **THEN** 结束无期限等待的界面状态并提示关闭程序后重开
- **AND** 不显示已成功中止，也不允许旧新会话并行操作

#### Scenario: Native cleanup blocks the UI thread
- **WHEN** 中止期间原生同步释放阻塞 UI，无法处理超时提示或关闭点击
- **THEN** 独立看门狗在中止受理后八秒内退出本应用进程
- **AND** 不终止外部浏览器或厂商进程，不删除已完成文件或保存的设置

### Requirement: Each explicit restart isolates the preceding session

系统 SHALL 在中止成功后的明确启动中重新识别当前 Key，建立新的业务及浏览器会话，不继承前一轮登录 Cookie、网页存储、证书选择状态、公司信息、页面任务或自动操作授权。旧会话迟到的事件 MUST NOT 更新新界面或触发新会话操作。会话内保留单次提交和错误保护；中止不重置设备或银行的密码错误次数。

#### Scenario: Swap from Key A to Key B
- **WHEN** A 完成业务并中止后，用户插入 B 并重新启动
- **THEN** 使用当前设备进入新认证流程，公司信息只由 B 本轮页面确认
- **AND** 不短暂显示 A 的公司，也不直接沿用 A 已登录页面

#### Scenario: Late completion after a new start
- **WHEN** 旧轮登录、查询、下载或设备事件在新轮开始后返回
- **THEN** 不改变新轮按钮、公司、状态或结果，不发起额外提交

### Requirement: Closing the application has a bounded completion time

系统 SHALL 在用户关闭程序时立即进入退出状态，停止接受业务命令，并在受理关闭后八秒内退出应用进程；清理异常、永不完成任务或浏览器释放阻塞 MUST NOT 导致永久取消关窗。关闭 SHALL 复用会话撤销和资源释放流程且不重建会话。超时退出只能影响本应用拥有的进程及资源，不得终止外部浏览器或其他程序。

#### Scenario: Close after login or while exporting
- **WHEN** 用户在已登录空闲、登录未返回或导出中的任一状态关闭 exe
- **THEN** 停止后续自动业务并在八秒内退出
- **AND** 已完成文件与保存的设置保持完整

#### Scenario: Close during stop or close repeatedly
- **WHEN** 中止尚未完成时关闭窗口，或连续点击关闭
- **THEN** 合并到单一退出流程，不重复释放，不重新开始八秒退出预算

### Requirement: Lifecycle diagnostics do not expose credentials

系统 SHALL 记录会话开始、中止、清理阶段、耗时、超时及退出结果，用于定位关窗卡住问题；日志 MUST NOT 记录 PIN、完整账户、公司名称、页面正文或带凭据的 URL。

#### Scenario: A cleanup phase exceeds its deadline
- **WHEN** 某清理阶段未在总期限内完成
- **THEN** 诊断能够指出阶段与超时结果，同时不包含业务秘密或完整身份数据
