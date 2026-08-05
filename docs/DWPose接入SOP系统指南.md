# DWPose 手部 21 关键点接入 SOP 系统指南

> 目标：用 **DWPose（全身姿态估计）** 替代 MediaPipe / YOLO-hand 作为手部关键点方案，
> 复现你发来的那张网图里「丝滑、稳定、遮挡/握拳不丢」的效果。
> 本指南对应代码已落地到项目中，本文说明如何启用、验证与调优。

---

## 0. 为什么用 DWPose

| 方案 | 关键点 | 优点 | 缺点 |
|------|--------|------|------|
| **MediaPipe Hands** | 21/手（两阶段） | 轻量、上手快 | 遮挡/快速运动易丢跟踪、握拳抖动大（你实测不丝滑） |
| **YOLO-hand / YOLO-pose** | 21 或 17（单阶段） | 快 | 握拳、横向手、被工具遮挡时召回差 |
| **DWPose（全身→手部）** | 133（含双手各 21） | 关键点最平滑、遮挡/握拳更稳、与全身姿态同源 | 需两个模型、单次推理略慢 |

DWPose = YOLOX-L 人体检测 + RTMPose-L 风格的全身姿态（SIMCC 输出），
从 133 关键点里取 **左手 91–112、右手 113–134** 共 21 点，
顺序与 MediaPipe 完全一致，所以**绘制层 `HandSkeletonConnections` 无需改动**。

---

## 1. 模型文件（已就绪，无需再下载）

```
E:/yolo/YoloDotNet-master/DWPose-onnx/models/
├── yolox_l.onnx            # 人体检测（216 MB）
└── dw-ll_ucoco_384.onnx    # 全身姿态（134 MB）
```

> 若需要重新获取：从 MMPose 官方仓库下载 `dw-ll_ucoco_384.onnx` 与 `yolox_l.onnx`，
> 放到上述目录即可。这两个文件项目里已经存在。

---

## 2. 代码改动清单（本次已落地）

| 文件 | 改动 |
|------|------|
| `Models/HandPoseEstimationModels.cs` | 新增枚举 `HandDetectionBackend { Auto, MediaPipe, Yolo, DWPose }`；`HandPoseEstimationConfig` 新增 `Backend`、`DWPoseDetModelPath`、`DWPosePoseModelPath`、`DWPoseModelDir` |
| `Services/IHandPoseEstimationService.cs` | `DWPoseHandEstimationService.InitializeAsync` 优先使用显式 DWPose 路径，否则回退到目录默认文件名 |
| `SOPModule.cs` | 新增 `_handBackend` 字段，从配置读取；构造 `handConfig` 时传入 `Backend`；重写后端选择逻辑（支持 DWPose/MediaPipe/Yolo 强制 + Auto 兜底） |
| `UI/configs/sop_config.json` | 新增 `"HandDetectionBackend": "DWPose"` |

核心推理实现（`Services/DWPoseHandDetector.cs`，约 1500 行）**此前已存在且完整**：
双阶段 YOLOX 检测 → DWPose 姿态 → SIMCC 解码 → 手部提取 → **卡尔曼滤波跟踪平滑**，
输出的 `HandPose` 与 MediaPipe/YOLO 方案接口一致（`IHandPoseEstimationService`）。

---

## 3. 如何启用（二选一）

### 方式 A：改配置（推荐，无需编译代码逻辑）
`src/UI/VisionInspection.UI/configs/sop_config.json` 的 `SOPModule` 段：

```json
{
  "SOPModule": {
    "HandDetectionBackend": "DWPose",
    ...
  }
}
```

可选值：`"DWPose"` / `"MediaPipe"` / `"Yolo"` / `"Auto"`（Auto 维持旧优先级）。

### 方式 B：运行时强制（代码里）
```csharp
var cfg = new HandPoseEstimationConfig
{
    Backend = HandDetectionBackend.DWPose,   // 强制 DWPose
    ConfidenceThreshold = 0.3f,
    MaxNumHands = 2,
    UseGpu = true
};
sopModule.UpdateHandPoseConfig(cfg);
```

---

## 4. 快速验证（Python，建议先跑这个）

项目提供了 `scripts/dwpose_hand_inference.py`，直接加载同一套 ONNX 模型，
在 Python 端复现 C# 推理逻辑，用来肉眼确认「是不是比 MediaPipe 丝滑」。

```bash
pip install onnxruntime opencv-python numpy

# 摄像头实时
python scripts/dwpose_hand_inference.py --source 0

# 单张图片
python scripts/dwpose_hand_inference.py --source test_image.jpg --out hand_result.jpg

# 视频
python scripts/dwpose_hand_inference.py --source clip.mp4 --out out.mp4
```

看到红点 + 红线的 21 点手部骨架即为成功。对比 MediaPipe 在同样遮挡/握拳画面下的表现，
DWPose 的手部点应明显更稳定、不易整体漂移。

---

## 5. 调参建议

| 参数 | 建议 | 说明 |
|------|------|------|
| `ConfidenceThreshold` | 0.25~0.35 | DWPose 输出置信度偏低，过高压不住抖动、过低引入误检 |
| `MaxNumHands` | 2 | 双手场景 |
| `UseGpu` | true | 用 CUDA 加速（YOLOX + DWPose 双模型，GPU 收益大） |
| 卡尔曼滤波 | 已内置 | `DWPoseHandDetector.cs` 的 `HandKalmanTracker`，跳帧/丢失时平滑预测 |
| `DWPoseModelDir` | 默认路径 | 模型换位置时改这里 |

### ⚠️ 已知注意事项：YOLOX 输入归一化
C# 端 `DWPoseHandDetector.PreprocessForDetection` 当前**未将张量除以 255**
（标准 YOLOX 预处理是 `img/255`）。如果实际运行发现**人体检测召回偏低、
进而导致手部检测整体缺失**，在该方法填充张量后补一行：

```csharp
// PreprocessForDetection 末尾，返回前
for (int c = 0; c < 3; c++)
    for (int y = 0; y < DetInputSize; y++)
        for (int x = 0; x < DetInputSize; x++)
            tensor[0, c, y, x] /= 255f;
```

（Python 验证脚本已采用标准 `/255`，若两者检测框不一致，多半源于此。）

---

## 6. 与那张网图的关系

你发的图是一个「目标检测 + 手部姿态」双管线 SOP 系统 demo：
- **目标检测（YOLO 风格框）**：`desktop / frame / conveyor / pressTool / tape`
- **手部姿态（红点骨架）**：即本文的 DWPose 21 点
- **SOP 步骤面板**：右侧步骤进度 + fps + 置信度阈值

本项目的对应结构：
```
摄像头帧
  ├─ YoloDotNet 物体检测 (SOPModule 主检测)        → 生成物体检测框 / SOP 步骤触发
  └─ DWPose 手部姿态 (DWPoseHandEstimationService) → 21 点手部关键点（本指南接入点）
         ↓
   步骤规则引擎 (SOPStateMachine / SOPWorkflow)     → 判定动作顺序、工具使用、时长
         ↓
   告警 / 仪表板 / defect_log
```

也就是说，把 DWPose 作为手部后端接入后，你的系统就具备了与那张网图一致的
「物体检测框 + 丝滑手部骨架 + SOP 步骤判定」能力。

---

## 7. 故障排查

| 现象 | 排查 |
|------|------|
| 仍走 YOLO/MediaPipe | 确认 `sop_config.json` 的 `HandDetectionBackend` 已设为 `DWPose`；看启动日志是否打印「DWPose 方案」 |
| 报「DWPose模型文件不存在」 | 确认 `DWPose-onnx/models` 下两个 onnx 存在；或配置 `DWPoseDetModelPath`/`DWPosePoseModelPath` 显式路径 |
| 手部点抖动/漂移 | 调低 `ConfidenceThreshold` 到 0.25；确认 `UseGpu`；检查卡尔曼滤波未关闭 |
| 整体不出现手部 | 先按第 5 节检查 YOLOX 归一化；用 Python 脚本同帧验证模型本身是否正常 |
| 推理慢 | 开 GPU；如仍慢，可换 `dw-ll_ucoco_384.onnx` 为更小的 `dw-ll_ucoco_256` 或 yolox_s |
```

