# 登录后已知弹窗兼容（2026-10-09）

## 现场根因

此 Key 对应账号登录后的主框架实际地址为 `/NCCB/NECV6B2BMainPlat_08_LH`，子框架为 `_08_FW`。之前自动关闭脚本把唯一支持地址写成 `_09_LH`；页面遍历虽已发现新页面，关闭脚本却在地址检查处直接返回 unsupported。

企业手机银行推广的 `.mob_drain`、`top_sjyl_type1` 和关闭图片 `mob_drain_close.png`，以及温馨提示的 `noticeTip` / `ccbWin` / `ccbWinColse` 结构都与旧页面相同。区别是账号使用的首页路由，不是 Key 弹出新的本机窗口。

## 修复与验证

关闭脚本支持批准网银壳页下、同源 `/NCCB/` 页面中的两种已知弹窗，不再依赖首页版本号。保留 HTTPS、银行主机、当前壳页、可见框架和具体弹窗结构检查。诊断消息来源同步支持同源银行子页面。新窗口拦截规则保持不变。

仍调用银行原有关闭按钮，不直接删除遮罩，不勾选“不再提醒”，不自动关闭业务确认。没有扩大到任意弹窗。

新增新路由回归先失败后修复；真实页面注入更新后的关闭脚本，推广面板和温馨提示关闭控件的可见数量均降为 0；应用日志也记录 notice-clicked 与 mobile-clicked。

最终自动测试：.NET 1379/1379、浏览器 255/255 通过。证据在 `artifacts/verification/popup-compatibility-20261009/`，`before.json` / `after.json` 只含脱敏结构，不含表单值。源码与正式 App DLL 已更新，SHA-256 为 `15BAC6A5DBC88D00F48FBD39E755D7E6060469EFFEBC8E8985BD490E8F5B1791`，发布前备份为 `artifacts/backups/before-popup-compatibility-20261009-163743`。

16:39 用户完成 Windows 管理员确认后，认证组件安装退出码 0，普通模式程序 PID 70068 自动启动登录。正式版本日志依次记录网页登录 Submitted、温馨提示 notice-clicked 和企业手机银行推广 mobile-clicked；两种弹窗在正常流程中均已自动关闭。临时调试端口已关闭，程序保持打开。
