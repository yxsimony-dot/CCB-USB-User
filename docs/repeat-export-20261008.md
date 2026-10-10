# 连续导出停留在选择账户阶段（2026-10-08）

现象：第二次或第三次导出时，页面已显示日期查询，上方一直提示正在选择第一条活期账户。

实机日志证据：09:49:36 首次导出经过 selected/accounts、queried/query 后成功下载；09:51:04 的导出在 open-accounts/opened 后，先出现 select-first/waiting/results，再持续 select-first/waiting/query，直到 09:51:34，期间没有 selected/accounts。日志只读取固定阶段元数据，没有采集 PIN 或账户内容。

根因：等待账户列表的代码把旧 query/results 也视为 accounts 已就绪。银行 taskBar.createWin 对已存在标签先 switchWin 再 submitWin，旧内容在新请求完成前仍可见。select-first 对旧内容执行 BacktoFirst，额外返回请求会干扰原账户列表请求，最后停在本次尚未选择过账户的日期页。不能只因为看见日期字段就跳过第一条账户身份确认。

修复：

- 等待 accounts 时只接受真实 accounts 阶段。
- select-first 遇到旧 query/results 只等待，不执行 BacktoFirst，保留打开账户入口时提交的原始导航。
- 第一条账户选择完成后，将进度改为“正在打开日期查询页…”，随后按原规则核对账户并填写日期。
- 删除已不再产生的 result-reopen 错误提示与日志码。

验证：

- 协调器连续三次导出测试，在修改前发现“仍是 results 就提前调用选择”的错误；修改后完整查询与下载顺序通过。
- JS 连续三次导出测试模拟旧结果页、旧日期页、列表延迟加载、同标签跳转日期页；修改前发现错误返回点击，修改后账户选择和日期提交各执行一次，月份更新正确，未选择时不能提交旧日期页。
- 更新原本要求点击 BacktoFirst 的三项测试，改为验证旧文档不产生额外导航。
- dotnet test UsbWebLauncher.sln -c Release --no-restore：1020/1020 通过；仅有现存 CS0067 测试事件警告。
- node --test tests/browser/*.test.cjs：191/191 通过，记录在 artifacts/test-tools/browser-repeat-export-20261008.txt。
- 自包含 win-x64 更新包：artifacts/staging/repeat-export-20261008。

部署时检测到用户已关闭程序，已将更新包替换到现有快捷方式对应的 artifacts/publish/win-x64-manual-actions-20260930，仅 App.dll、App.pdb 发生变化。旧文件位于 artifacts/backups/before-repeat-export-20261008-095536。部署哈希与更新包一致，App.dll SHA-256 为 43334058AEA9DD34A17F6406DF012ECABBC3A7394446EE98603DED27994E8079。

已重新打开 EXE，确认主窗口存在且 Responding=True。没有自动启动业务或进行真实网银导出；真实连续导出效果仍待用户确认。启动业务前需点击一次“安装环境”刷新 App 程序集绑定。
