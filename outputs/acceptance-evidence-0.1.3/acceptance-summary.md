# CutMap 0.1.3 验收总表

验收日期：2026-10-28（Asia/Shanghai）
基线：`codex/acceptance-remediation-20261021`，起始 HEAD `9989b982bf9daf0454c0502a27494262732b55bc`。

| 阶段 | 状态 | 结论与证据 |
|---|---|---|
| 0 基线 | PASS | 既有基线、30 幅 DEM、参考包及工作区保护项已锁定；不覆盖用户的 `CHANGELOG.md`、`_memtest*`、`_ovr_*`、`_split_test`。 |
| 1 高程一致性 | FIXED_PASS | 修复 CTB `ctb-tile` 未将 `-r/-z/-m` 选项传给 `TerrainTiler` 的问题，并原子替换 `ctb-tile.exe` 与 `ctb.dll`。使用 Cesium 实际 SW-to-NE 三角插值的 25 点复验：最大绝对误差 0.269179 m、MAE 0.090603 m、RMSE 0.107689 m；`stage5-ctb-correction-probe.json`。 |
| 2 性能 | FIXED_PASS | 将 2 GiB 以下 DEM 保持为单个 CTB 进程，避免 1.09 GiB 输入被不必要拆分。三次有效全量复测的原生中位数 613.566 s、包装层中位数 577.525 s，额外开销 -5.87%，产物计数一致；`stage5-wrapper-benchmark.json`。 |
| 3 DPI 与中文界面 | BLOCKED_ENVIRONMENT | 实机 150% DPI 下，14 个窗体布局自检为 0 问题，主窗口、切片、下载、打包截图见 `dpi-150/`。未改变用户系统缩放，无法真实复验 100%/125%/200%。 |
| 4 外部下载 | DEFERRED_EXTERNAL_SOURCE | 本地可重复验收门通过续传、取消、超时、异常、日志与历史验证，见 `download-local-gate-*.log`。官方 AWS 一幅真实 DEM 在 5 分钟内仅传输 1 MiB，保留 `.part` 断点文件；不修改下载源或重试策略。 |
| 5 发布工程 | PASS | 版本统一为 0.1.3。绿色 ZIP 与安装包均通过自包含载荷校验；独立解压、静默安装、启动及卸载均成功，日志见 `release-zip-layout-check.log`、`setup-*.log`。 |
| 6 安装包可信度 | PASS（风险已记录） | 发布安装包 Authenticode 状态为 `NotSigned`；不伪造签名。包内含 `THIRD-PARTY-NOTICES.txt` 与 `CTB-LICENSE.txt`。未签名分发可能出现 SmartScreen 提示。 |
| 7 Gitee Release | PENDING | 待最终提交、以最终提交号重建发布包、生成 SHA256SUMS 后执行。 |
| 8 自动化与需求追踪 | BLOCKED_ENVIRONMENT | 已实际运行本地下载验收门及 15 项核心工作流门（`workflow-gate*.log`），均通过。仓库没有可复用的托管 CI 配置，未添加未经 Gitee 平台验证的流水线。DataV 变更已由 `resources/boundary-manifest.json` 记录来源、原因、影响数据集与校验值。 |

## 发布前剩余动作

1. 审核并提交本轮验收直接相关的源码、版本和证据文件；保留用户既有未提交内容。
2. 用最终提交号重建绿色包和安装包，生成 `SHA256SUMS.txt`。
3. 推送目标分支并创建 `v0.1.3` Gitee Release；下载线上资产复算 SHA-256。

## 已知限制

- DPI 100%/125%/200% 仍需要具备相应物理缩放的 Windows 环境复验。
- Copernicus 官方源的真实端到端下载待网络可达时继续；本地续传、取消和超时逻辑已有通过证据。
- 发布包尚未代码签名；应在发布说明中明确 SmartScreen 风险。
