# 登录后两个已知弹窗的后台处理

用户在实机确认本次没有中央手机银行推广，并明确同意只处理另外两个。依据两轮本机结构采集，当前范围为独立公告和右下角温馨提示。第三类推广不作为等待或完成条件。主页面证据保存在 `artifacts/verification/known-popup-dismiss-20260930/live-discovery.json`。

## 实际行为

- 独立公告：仅当顶层位于已验证的建行证书入口页面，且网站自动请求已知 `https://www.ccb.com/chn/html1/subject/25/10/1029wy/html.html` 时，处理 NewWindowRequested 以阻止新窗口出现。地址精确匹配；用户主动打开、未知路径及业务窗口继续按浏览器原行为处理。
- 右下提示：仅在同源、指定主页面 `/NCCB/NECV6B2BMainPlat_09_LH` 内识别唯一可见 `div#noticeTip.ccbWin`，核对原来的标题及 `a.ccbWinColse` 关闭按钮后点击原按钮。“不再提醒”复选框不改变。其他提示、隐藏控件、重复候选不处理。
- 页面结构变化通过 MutationObserver 触发检查，文档就绪时检查已有弹窗；没有定时轮询、弹窗等待或“全部关闭”业务门槛。每个关闭控件最多点击一次；新文档或真正的新控件可以独立处理。登录协调器及 PIN/证书流程不变。

浏览器初始化时注册文档创建脚本，覆盖后续主文档及子 frame 导航。此注册在首次导航前完成，不是等待网页弹窗出现。[WebView2 官方说明](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.addscripttoexecuteondocumentcreatedasync)。原来的临时 WpfPopupDiagnostics 定时采集已从运行入口移除，不再持续采集页面结构。

已知处理状态只写固定事件名到本机 certificate-page-login.jsonl，不写消息原文、输入值或异常正文。点击事件只表示原按钮已被点击，不自动推断银行业务结果。

## 验证与交付状态

2026-09-30：全量 .NET 测试 873/873，Node + jsdom 脚本测试 71/71。新增公告策略测试 23 项、提示关闭行为测试 28 项，覆盖缺省/延迟出现、隐藏、重复候选、错误域名、用户主动打开、复选框不变、页面离开和 BFCache；新增正向行为先观察失败再实现。Windows x64 自包含发布成功。独立审查发现并修正初始化期间释放的竞态；运行入口已停止构造临时定时采集器。浏览器工具仍不可用，没有运行新的真实浏览器自动关闭试验。微任务仅用于合并检查，不承诺晚于所有网页的 DOMContentLoaded 监听器；本次真实控件记录为内联 onclick。

用户确认关闭程序后，已更新原入口 App DLL/PDB，逐一验证部署 SHA-256 与暂存一致；唯一快捷方式和唯一主程序 EXE 已核对。记录见同验证目录 result.json、staged-sha256.json 与 deployed-sha256.json。重开后依次点击“安装环境”刷新代理哈希绑定，再点击“启动业务”，无需重设 PIN。真实银行页面的自动关闭效果仍需用户更新后实测；已有结构证据与模拟测试不等同于实际关闭成功。


