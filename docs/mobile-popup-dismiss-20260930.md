# 中央手机银行推广自动关闭

用户实测确认前两个弹窗已不再出现，随后截图显示中央企业手机银行推广再次出现。本次继续沿用事件驱动、不等待弹窗的方式补上第三类。

## 依据与范围

前次 14:33 的只读采集已包含隐藏的完整推广模板：同源 `/NCCB/NECV6B2BMainPlat_09_LH` 文档有两个 `.mob_drain`，一个为空遮罩，另一个内容容器含 `#top_sjyl_type1` 等推广分区。关闭元素是图片 `https://b2bstatic3.ccb.com/V6/STY6/CN/images7/FN90001/mob_drain_close.png`，原事件函数记录为 `$`。它没有通用 close 类名。证据及本次已拦截公告的固定事件见 `artifacts/verification/mobile-popup-dismiss-20260930/mobile-template-evidence.json`。

本次未重新连接当前可见页面 DOM；定位依据是已采集模板与用户当前截图。不能将其写成已执行真实中央推广关闭。当前日志只确认独立公告被拦截，没有提示框安装或点击事件；用户确认另外两类未弹出，但这不能单独证明右下提示曾执行自动点击。

## 改动

仅在原批准的 LH 页面中，选择唯一可见且含唯一 `#top_sjyl_type1` 的 `.mob_drain` 内容区域，再校验唯一指定银行关闭图片并点击其原有 handler。遮罩交由页面原处理函数清理；不删除 DOM、不改“不再提醒”、不触发服务介绍或开通。重复、隐藏、来源错误或多义控件不点击。提示框和推广层独立检查，任一未出现都不阻挡另一类。

补强旧式 frame 的事件接入：在文档创建及顶层 DOMContentLoaded/NavigationCompleted 时注册或补查；通过同源实际文档及 frame load/DOM 变更发现登录后替换的主页面。最多 4 层、16 文档，按实际文档地址校验，禁止 eval 和定时轮询；具体关闭仍需通过精确页面与控件校验。每文档安装幂等，pagehide 停止，BFCache 恢复。异步刷新忙碌时不排队，释放后不报告迟到结果。

固定事件优先通过同源顶层 WebView2 通道写入本机日志，弥补旧式 frame 消息可能漏接；只记录固定枚举 `popup-bootstrap-ready`、`popup-notice-installed`、`popup-mobile-clicked` 等，不输出网页消息原文。没有把缺少旧消息武断判为某个已证实的浏览器根因。

## 验证与交付

全量 .NET 最终 877/877 通过；Node + jsdom 脚本 115/115 通过，含组合运行实际 installer 与 bootstrap 的旧式 frameset 回归。新增回归覆盖同源窗口传入、延迟推广、两类同时出现、checkbox 两种原始状态不变、原 handler 清遮罩、精确图片匹配、隐藏/重复拒绝、移除 frame、销毁和 BFCache。新增正向用例先失败后实现。

首轮并行执行时原有 `CancelledPhysicalNavigation_StopsOldNavigationBeforeStartingTheNextOne` 单项失败；未修改其业务实现或测试，随后单项及无 Node 并行负载的完整 .NET 复跑均通过。原 CS0067 编译警告仍在。真实中央推广关闭尚未实机验收。

Windows x64 自包含发布已构建至工作区外。发布前后状态与哈希见本次验证目录；继续更新唯一原入口，需关闭运行中的 EXE 后替换 App DLL/PDB，随后点击“安装环境”刷新代理哈希绑定，再启动业务。无需重新设置 PIN。

部署完成：发布前检测主程序已退出，直接更新原目录 App DLL/PDB，并验证两项 SHA-256 与暂存清单一致。唯一快捷方式、唯一主程序 EXE 均已核对。部署记录为 result.json 和 deployed-sha256.json，实机中央推广关闭仍待用户确认。
