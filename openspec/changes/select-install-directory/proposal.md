## Why

用户要求安装前选择安装路径。当前安装器隐藏目录页，事务入口只允许固定 LocalAppData 目录，无法选择其他磁盘。

## What Changes

- 首次安装显示中文目录选择页，默认沿用旧路径，用户可选择可写的本地磁盘程序子目录。
- 在安装和组件变更前检查选择的目录；支持中文与空格，拒绝危险路径和个人文件混放。
- 升级显示并沿用 HKCU 已登记的安装目录；换位置需先正常卸载，避免多目录争用认证组件。
- 更新界面和文档显示新的路径规则；生成、测试并交付新版安装包。

## Capabilities

### New Capabilities
- `installer-directory-selection`: 安装前选择目录并为后续升级保持一致。

### Modified Capabilities

## Impact

Inno 安装向导、PowerShell 安装事务入口、路径检查测试、更新窗口提示和用户文档。认证组件继续按实际程序路径绑定；PIN、设置及 PDF 存储规则不变。
