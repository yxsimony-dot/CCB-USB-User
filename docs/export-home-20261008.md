# 导出前返回首页（2026-10-08）

用户反馈：从交易明细等其他页面点击“导出流水”，流程没有先回首页，停留在选择第一条账户阶段。

原流程从 open-accounts 开始，直接点击活期账户入口。新增 ensure-home 前置阶段，通过银行现有首页导航点击事件返回首页，再等待首页内容加载完成；原账户选择、日期查询、身份核对和 PDF 下载规则保持一致。

结构依据为本机银行页面缓存中的 taskBar 与主菜单脚本：顶部菜单 #menu1Content 下的 a[id="0000"]；首页任务标签 task_0000，激活类 taskOn_div；内容 iframe win_0000，加载事件设置 isReady="true"。另外核对 iframe 实际文档在原允许的同源文档集合内且 readyState 为 complete。不是通过匹配任意“首页”文字定位，因此不会点击分页首页或标签关闭按钮。

每次导出最多点击首页一次，点击前记录本次运行状态，防止银行事件同步重入或轮询重放。首页已激活时只检查内容就绪，不重复跳转；缺失、禁用、重复或隐藏的导航会停止操作。首页等待沿用 30 秒阶段超时以及导出整体取消/超时。

验证：

- 新增 3 项协调器测试，修改前均失败；覆盖首页就绪前的执行顺序、失败和取消后不得操作账户。
- 新增 5 项 JS DOM 测试，修改前均失败；覆盖查询页返回、延迟加载、首页直接继续、旧隐藏结果页、分页误匹配、重复/隐藏/禁用入口、重入、过期、清理、运行冲突和连续多次导出。
- dotnet test UsbWebLauncher.sln -c Release --no-restore：1020/1020 通过。现有 CS0067 未使用测试事件警告仍存在。
- node --test tests/browser/*.test.cjs（jsdom 26.1.0）：190/190 通过。输出位于 artifacts/test-tools/browser-tests-20261008.txt。

新包生成至 artifacts/staging/export-home-20261008。用户确认关闭程序后，已于 2026-10-08 替换到现有启动目录 artifacts/publish/win-x64-manual-actions-20260930，仅 App.dll 和 App.pdb 的内容变化，配置与其他文件未改动。部署后 SHA-256 与已验证更新包一致，App.dll：7366C9E0931B7809B03A4069F5E39590ED3A717DB737A673BC65E322C54D0486。替换前文件备份至 artifacts/backups/before-export-home-20261008-094850。

已打开现有目录中的 EXE，确认 USB Web Launcher 主窗口存在且 Responding=True。尚未启动业务或操作真实网银导出；真实页面导出仍待用户确认。更新了 App 程序集后，需在本版本中点击一次“安装环境”刷新认证代理的文件哈希绑定。
