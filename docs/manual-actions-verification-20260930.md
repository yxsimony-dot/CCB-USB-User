# 三个独立操作入口验证记录

日期：2026-09-30。OpenSpec：`separate-manual-launcher-actions`。

后续整理：用户确认交付并授权归档后，又要求只保留本次三按钮版本入口。现统一通过根目录 `启动 USB Web Launcher.lnk` 启动，目标仍为本记录中的新发布包。旧发布包、修改前源码备份及测试运行副本已按用户要求从工作目录移至回收站；本文“保留与回滚”节描述的是交付当时的状态，现不再作为可用回滚说明。源码、验证结果和 OpenSpec 历史继续保留，实机验收事实不变。

## 交付内容

- 发布包：`artifacts/publish/win-x64-manual-actions-20260930/UsbWebLauncher.App.exe`。
- 上方提供安装环境、设置 PIN、启动业务三个主入口，以及环境和 PIN 维护菜单。
- 启动 EXE 不自动监听业务设备或打开网银；明确启动后使用既有协调器。
- PIN 保存仍使用既有存储服务；应用层延后业务通知，准备操作不隐式安装或导航。
- Core、Windows、NativePromptBroker 源码及原配置未改动。修改文件清单和发布哈希位于 `artifacts/verification/manual-actions-20260930/`。

## 自动验证

- 修改前基线：773/773 测试通过。
- 最终全量：804/804 测试通过，0 失败，0 跳过。命令：`dotnet test UsbWebLauncher.sln -c Release --no-build`；此前已执行完整 Release 构建。
- 编排测试先以尚未实现的入口运行，记录预期失败；PIN 拆分、界面可用状态及认证门测试均记录失败后修复。
- 一次全量运行中，原有 `CancelledPhysicalNavigation_StopsOldNavigationBeforeStartingTheNextOne` 在 1 秒等待处超时；未修改该测试或浏览器代码。该项单独复跑与后续全量均通过，保留此观察记录。
- 原有 CS0067 警告仍存在：`ClientCertificateAuthenticationEndToEndTests` 中未使用的 `NavigationStarting` 事件。
- OpenSpec 严格校验通过，4 份规划产物齐全。
- App 和 NativePromptBroker 均成功发布为 Windows x64 自包含包。包内配置与源配置哈希相同，原银行扩展 manifest 与 broker 策略文件存在。

## 真实窗口检查（无设备冒烟）

使用新发布包的 `--smoke-test-no-devices`，通过 Windows UI Automation 读取/操作本次测试进程，并捕获实际窗口：

- 三个主入口、两个二级菜单入口可见且可达。
- 初始显示“业务未启动”和“USB Key 未检测”，网页未打开。
- 检查默认尺寸及 900×600 最小尺寸，按钮和状态文字完整可见。
- 环境菜单显示安装/更新、卸载；PIN 菜单显示更换、删除。未执行这些维护操作。
- 窗口正常关闭，退出码 0。
- 隔离运行副本使用错误配置触发初始化失败，恢复配置后连续点击重试；初始化恢复后仍保持待启动，不自动执行业务。
- 隔离运行副本移除认证配置，明确点击启动业务后进入等待 USB 状态，正常重复启动禁用，准备按钮显示不适用；正常关闭退出码 0。
- 脚本、结果 JSON 和截图位于 `artifacts/verification/manual-actions-20260930/`。验证脚本先以 UTF-8 无 BOM 保存，Windows PowerShell 5.1 解析中文时报错；改为 UTF-8 BOM 后运行通过，该问题不涉及产品代码。

本轮没有安装/卸载本机代理，没有读取或提交真实 PIN，没有访问真实网银认证流程。自动测试使用替身与合成数据；无设备冒烟不能替代实机认证。

## 待用户实机确认

OpenSpec 任务 4.3 保留未勾选。请使用新包执行：

1. U 盾已插入时打开 EXE，确认仍等待明确启动。
2. 点击安装环境，按现有流程确认本机组件安装；更新应用后代理需要重新绑定新路径和文件哈希。
3. 如需设置或更换 PIN，在本机界面操作，确认保存后不自动开始认证。
4. 点击启动业务，确认原有证书选择、密码窗口处理和 TLS 成功。
5. 关闭重开后先点启动业务再插 U 盾，确认等待与后续执行正确；正常业务中拔插沿用原规则。

真实硬件成功前不勾选该项，也不据此改变其他 OpenSpec 变更的验收状态。

## 保留与回滚

- 修改前源码与测试副本：`handoff/manual-actions-before-20260930/`。
- 旧发布包保留：`artifacts/publish/win-x64-pin-input-confirmed-20260929/`。
- 新版与旧版使用原有本机凭据机制，无配置迁移。回滚时关闭新版，运行旧包并重新安装与旧包匹配的认证组件，不放宽代理的路径/哈希检查。
