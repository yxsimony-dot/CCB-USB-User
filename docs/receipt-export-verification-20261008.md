# 电子回单导出实现与验证

OpenSpec：`add-electronic-receipt-export`。范围：本年所选月、与流水一致的第一条活期账户，仅第一页；不翻页、不点“下载全部”。银行原 PDF 保存为 `公司全名建行账号后4位回单.pdf`，同名递增序号。

## 实页证据

用户在临时采集版手动查询并下载后回复“已下载”。脱敏记录保存于 `artifacts/verification/receipt-discovery-20261008/live-evidence.jsonl`。这证明人工下载链路，不代表新按钮已经实机验收。

- 查询文档：`/NCCB/NECV6B2BMainPlat_09_FW`，`jhform`，原 `go` 提交处理器。字段包括 `hdType`、`BEGIN_DATE`、`END_DATE`、`ACCT_NO`、`PRINT_FLAG`、`DEAL_DIRECTION`、`MIN_AMT`、`MAX_AMT`、`DOC_NO`、`Data_Fmt`、`Mrg_Dwld_Ind`，提交按钮 `subbutton/Submit1`。
- 结果文档：`/NCCB/NECV6B2BMainPlat_09_LH`，同一业务 frame 替换文档。结果行 `KEYVALUE`/`trClick`；全选 `all`/`selectall`；打印 `Submit8`/`HDPrint`；不使用 `sc2`/`downAll`。
- 本次实际是单页结果，`.splitPage` 中上一页为无点击处理器的 `span`。`.inPage` 是跳页输入框，不能用它的值证明当前页码。多页和空结果为基于该结构的合成负向/边界测试，尚无对应实页验收。
- 16:03:25 原 `LH` POST 后，主 WebView 对 `/NCCB/NECV6FileDownload_09_LH` 发起 POST，返回 attachment，主页面 `DownloadStarting` 后完成。不是流水使用的下载子窗口；此前出现的推广新窗口与回单无关。
- 原始下载扩展名 `.pdf`，79,912 字节，文件头 `%PDF-1.6`；未保存或输出正文、原文件名、公司或账号值。响应 MIME 只记录为脱敏类别 `other`，不能据此声称实页返回了某个具体 MIME。
- 采集未记录选项值。运行时根据可见选项“批量回单查询”“全部”“PDF”“是”及首页账户/公司匹配，读取并保留银行原值；完整账号仅停留在页面本次运行状态中，跨到宿主的身份只有公司名和末四位，用于命名。

查询字段已完整采集，查询控件列表曾截断，结果列表未截断。首页身份结构沿用已有公司展示与流水验证模板。合成 fixture 中的业务值、radio 文本关联包装和分页变体不是银行原 HTML 的完整复制。

## 实现

`CcbReceiptExport.js` 使用独立 runId/页面状态，返回首页后导航到回单，显式设置整月、批量、全部状态/方向、PDF、合并是，并清空金额/凭证/模糊筛选。提交和打印在调用原处理器前标记已消费，核对新结果的 frame 归属、账户、日期、筛选及第一页。已全选不反选；无结果不建立下载接收器。

`ReceiptExportCoordinator` 在打印前注册 `WpfReceiptDownload`，等待完成并清理后释放页面状态。阶段 30 秒、下载 90 秒、总流程 120 秒。两个按钮共享 ViewModel 门闩，首个状态通知前占用，重复/交叉点击不排队；月份、年份和目录固定为开始时的值。

回单接收器限定原 HTTPS 银行 origin 和已观察到的无 query 下载路径，获取 deferral 后回读本次已消费打印及原结果文档。明确的 HTML/JSON/图片/压缩包等类型拒绝；非标准类型必须有 `.pdf` 建议扩展名，最终仍必须下载完成且文件头通过 PDF 检查。只写独立临时文件，完成后原子移动并处理重名。

正式版撤除 `--receipt-discovery` 及自动接入；保留诊断类与脱敏隐私测试，无自动采集实例化。PIN/Windows 认证组件不在本次改动范围，部署时另行逐项核对哈希。

## 自动验证和交付状态

协调器回归先因缺少新类型失败，实现后 7/7 通过，覆盖动作顺序、打印前接收、空结果、不合法身份、取消、异步清理和重复运行。最终 .NET 1182/1182（receipt-export.trx）、Node 230/230（node-tests.txt，其中回单21项），OpenSpec strict 通过。保留既有 CS0067 测试事件警告。独立 DOM/下载审查记录在验证目录 review*.md；无新增代码阻断。

真实 WebView2 合成探针在本地拦截全部资源，验证主页面两次 POST 附件、deferral 内 ExecuteScriptAsync 核对、标准/非标准 MIME、字节保持、碰撞命名及无临时残留。探针没有访问真实银行。无设备暂存启动进程检查通过，未做原生 UI 自动化或声称完整视觉检查。

最终暂存：artifacts/staging/receipt-export-20261008。与唯一发布目录相比，仅 App.dll/PDB 不同，App.dll SHA-256 为 186BF111EDA2872DEAD0EBAF66937DFF710B6D755DDAA8A7E14B65C645B457A3。Windows.dll、Core.dll 和原发布 broker EXE 哈希与已验收 PIN 版一致。

正式版已于 2026-10-08T16:45:21.2108723+08:00 在用户确认关闭后部署并以正常模式重开；新按钮真实银行验收未进行。用户此前确认 PIN 大写问题已修复，此结论沿用，不重复改动输入方案。

独立审查保留的限制：首页首账户与活期列表首行在任意多账户配置下的排序尚未证明，当前单账户配置需现场验收；下拉原值与隐藏结果值若银行编码不同会拒绝。不会以末四位相同放宽账户匹配。

## 正式部署

仅替换原发布目录 App.dll/PDB，逐项与暂存哈希一致。备份：`F:/USB-Web-Launcher/PROJECT/artifacts/backups/before-receipt-export-20261008-164521`；部署记录：`artifacts/verification/receipt-export-20261008/deployment.json`。原快捷方式目标及无参数启动保持一致，重开进程 PID 52856。

使用新版前由本人点击“安装环境”刷新程序集哈希绑定，再“启动业务”，登录后选择月份并点“导出电子回单”。不需要重新设置 PIN。真实银行首次/重复/交替及多页只第一页验收保持任务5.3未完成。

恢复方法：关闭应用，复制上述备份中的 App.dll/PDB 回原发布目录，核对 previousFiles 哈希，随后正常启动并通过“安装环境”刷新绑定。备份是临时采集版的程序集，正常无参数启动不启用采集。不要添加 --receipt-discovery。

启动纠正：首次启动误用了 Hidden，用户报告未看到窗口。已用 WindowStyle Normal 重新启动 PID 52856，确认存在 USB Web Launcher 主窗口，并停止此前隐藏副本。
