# 大写 PIN 与 Caps Lock 修正（2026-10-08）

用户确认：同一已保存 PIN，在 Caps Lock 关闭时提示密码错误，开启时能全自动成功。这与厂商键盘回调依赖 GetKeyState 的大小写状态一致；不再将问题归因于保存的 PIN 被改写。

## 实现

以用户要求恢复的单次 SendInput 批量输入为基线，撤除持续 AttachThreadInput 和逐字符 ACK 发送。对于至少含一个大写字母、其余全部为 ASCII 大写字母或数字的 PIN，在输入前短暂连接目标线程，确认 Caps Lock 开启后立即断开。映射显式使用 CapsOn，不依赖代理线程原来的 Caps 状态；不生成临时 Shift，需 Shift 才能输入的特殊键盘布局拒绝此路径。

密码原文不做大小写转换，不更改凭据存储。小写、混合大小写和纯数字 PIN 保持原有批量路径；本次验证不代表解决了所有混合大小写厂商控件问题。

发送完整批次后，继续核对窗口身份、前台及字符数。收到全部字符才恢复原 Caps 状态并继续点击确认。异常、取消或超时也尝试恢复；目标已失焦或被替换时不对其他应用发送切换键。Caps 切换部分发送失败时只补 key-up，绝不重发 toggle。输入/提交都不重试，不记录真实 PIN、键码或字符序列。

## 验证

回归先红后绿，记录位于 `artifacts/test-results/pin-caps-batch/`。覆盖原状态开/关、源字符保留、普通路径、准备失败、输入失败、超时恢复，以及真实 mapper/builder 的固定 Caps 映射与特殊布局拒绝。

最终补充物理 Shift/Ctrl/Alt/Win/Caps 按住时停止，以及未支持固定 Caps 模式的 builder 默认拒绝。全量 .NET 测试 **1119/1119** 通过，报告 `caps-batch-full.trx`；没有新增编译错误，保留原有未使用测试事件 warning。

隔离测试使用完整生产 executor、真实 SendInput、自建银行样式 EDIT、厂商消息转发模拟和已加载搜狗输入法。仅输入公开合成样例 `ABCD5789`，未操作真实银行窗口：

- 初始 CapsOff：93ms 成功，内部收到的八个字符逐字一致，结束后 CapsOff。
- 初始 CapsOn：83ms 成功，内部收到的八个字符逐字一致，结束后 CapsOn。

日志：`artifacts/test-tools/native-executor-probe/uppercase-caps-off.jsonl` 与 `uppercase-caps-on.jsonl`。这些日志中的样例/键值仅属于隔离 fixture，生产日志不记录密码内容。

最终版再次各运行一次：CapsOff 90ms、CapsOn 86ms，完整值和 Caps 恢复均通过；记录 `uppercase-final-caps-off.jsonl` / `uppercase-final-caps-on.jsonl`。fixture DLL 与正式发布 DLL 哈希一致。

## 发布边界

只更新原发布目录内 Windows DLL/PDB 与 broker EXE/PDB，不替换 App/Core，不改变公司面板、导出或用户 PIN。受保护的已安装 broker 必须通过界面“安装环境”刷新文件和哈希绑定后才使用新逻辑。真实银行最终验收仍待用户正常操作确认。

15:07 已部署五个增量文件并逐一核对 SHA256。备份及记录：`artifacts/backups/before-pin-caps-batch-20261008-150712/deployment.json`。Windows.dll=`C5D26DFDDB976AF6B6D2794531BF9B7CDAD362AB7870DD9546998E47C1CADF68`；broker EXE=`8DA7EB7E348C403AD85634DEB7A91FCEEF7B5BA54F3D5E04D89D916DD4D9A9F5`。App/Core/配置/策略哈希不变。
