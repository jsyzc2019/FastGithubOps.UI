# Release 说明存档

本目录保存每个版本 Release 正文（GitHub Release 的 `body`）的 Markdown 源文件，
用于历史追溯与二次编辑。文件命名约定为 `v<版本号>.md`，例如 `v2.5.6.md`。

已发布版本：https://github.com/jsyzc2019/FastGithubOps.UI/releases

## 与发布脚本的关系

`create_release.py` 创建 Release 时，正文优先读取本目录下 `<tag>.md`；
若该文件缺失，则回退到脚本内嵌的 `FALLBACK_NOTES`（仅含使用说明的简短兜底文案）。

因此**发布新版本的正确顺序**是：

1. 在本目录新增 `v<新版本>.md`，写好完整的变更说明
2. 构建发布包并生成 `.sha256`
3. 运行 `create_release.py <tag> <附件...>` 上传

这样说明文本只有一份事实来源，且随代码一起版本管理、可以追溯。
