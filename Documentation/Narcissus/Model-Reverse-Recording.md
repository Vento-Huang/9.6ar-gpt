# 3D 模型：原版水仙倒放导出

新增入口：**Tools → Narcissus → 3D Model Reverse Recorder**。

这条路径直接调用现有 `NarcissusFaceGrowth.UpdateRecording`，由同一个 `NarcissusColonyLayout` 和 `NarcissusModel` 生成全部 38 株植物、根系、花冠避让数据与萤火虫。没有另建螺旋布局，也没有更换花朵模型。RecordedColony 保留原 shader 的两个 fragment 函数，仅将顶点从平面贴层投影改为三维投影，使后脑能遮挡脸前的植物。

## 操作

1. 在 GitHub Desktop 获取更新并切换到 `codex/model-reverse-recording`。
2. 打开现有 Unity 项目，停止 Play。打开上述菜单。
3. `Copy live effect settings` 自动选取场景的 `MidFaceEraseMask`，也可以手动拖入原组件。使用它的花朵亮度、萤火虫亮度、皮肤重建等参数。
4. 选择原始 `human portrait 3d model.glb`，再选择随本次交付提供的 `Tripo-Unity-Calibration.json`。原始模型与标定数据通过 SHA-256 匹配，避免用错人脸。
5. 点击 **Build / rebuild preview**，检查 0 秒、10 秒、13 秒。
6. 点击 **Export 13-second film**，选择输出目录。

输出 1920×1080、30 fps、390 张 PNG，时长 13 秒。若 Mac 的 `/opt/homebrew/bin/ffmpeg` 或 `/usr/local/bin/ffmpeg` 存在，工具自动合成 H.264 MP4；否则保留可导入剪辑软件的完整图像序列（从 `frame_00000.png` 导入，设为 30 fps）。

- 0–10 秒：头部固定中心自转一圈，原生长时间从 21 秒倒退到 0。
- 第 10 秒：花朵、根系和萤火虫全部退去，留下原版重建皮肤。
- 10–13 秒：停止在正面，五官颜色与几何形状逐渐恢复。

布局随机种子默认 1，等同于实时场景重置后的第一个 track。实时场景本来就会为不同 track 生成不同大小、朝向和错峰时间；要复用某次特定 track 的分布，可填入相同 track ID。模型脸型与关键点仍会影响相对位置。

## 数据适配与限制

- GLB 读取器针对本次提供的未压缩、单网格、单材质、无节点变换的 Tripo 导出。其他结构明确报错。
- 标定文件包含从该模型正面渲染检测出的 468 个关键点，不从实时摄像头采集。模型及标定文件不写入公共仓库。
- 原版皮肤重建和遮罩先在模型正面参考图上计算，再投影到三维表面。侧面深度平滑是为离线模型新增的适配，并非原实时二维遮罩的功能。
- 原版法线贴图/PBR 不用于人像：保留原始肤色图，避免照片自身光照叠加新灯光。植物保留原 shader 的光照计算。
- Live `Update` 的时钟、20 Hz 节流和跟踪路径不变。只有录制入口接受绝对时间并跳过节流。
- 工具当前是待 Unity 本机验证的版本。开发环境无 Unity 编辑器，尚未完成 Unity 编译、GPU shader、macOS 图像方向/颜色和视频成片验收；不能将托管测试通过视为 Unity 内效果已验证。

## 已完成的验证

- 实际 GLB 解码：983,681 个顶点、5,713,800 个索引，UV 数匹配。
- 原花朵/布局测试：20 个随机布局、花瓣外壳、附着短茎、关节变形、21 秒成熟终点通过。
- 106 项原皮肤状态和录制 seek 测试通过（托管边界替身）。
- Editor 导出代码及原生长代码通过 C# 语法/类型边界检查；此项不等于 Unity Editor 编译。
- RecordedColony 的花朵及萤火虫 fragment 函数与原 shader 文本一致。
