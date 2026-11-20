# Release 说明存档

每个 `vX.Y.Z.md` 对应同版本的 GitHub Release 正文，随代码一起版本管理。已发布的版本见 [GitHub Releases](https://github.com/FluxForceDynamics/FastGithubOps.UI/releases)。

1. 为新版本新增 `vX.Y.Z.md`，记录变更、下载方式和校验方法。
2. 更新 [`Directory.Build.props`](../Directory.Build.props) 的 `<Version>`，并提交、推送版本号和发布说明。
3. 按 [发布流程](../docs/本地发布流程.md) 选择 GitHub Actions 或本地脚本，构建并发布对应版本。

本地脚本要求对应的版本说明文件存在。CI 和已有的 `create_release.py` 同样优先使用这里的正文，缺少时使用内置模板。
