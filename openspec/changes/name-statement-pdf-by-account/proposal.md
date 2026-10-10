## Why

用户要求以公司全名、建行和账号后四位识别流水文件，替换已验收导出流程的年月命名。

## What Changes

- 从本次查询账户的真实页面信息获取完整名称和账号末四位。
- 保存为公司全名建行账号后4位.pdf，保留重名保护。
- 名称与所选账户不一致或无法完整读取时停止，避免错误标记文件。

## Capabilities

### New Capabilities
- `statement-pdf-account-name`: 用对应账户的完整名称和末四位命名 PDF。

### Modified Capabilities
无。

## Impact

页面身份读取、导出协调器参数和文件命名策略。与同时进行的下载目录改动合并验证部署，不增加入口。
