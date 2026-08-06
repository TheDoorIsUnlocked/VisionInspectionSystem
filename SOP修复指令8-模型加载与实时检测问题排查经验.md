# SOP检测系统修复指令8：🧠模型加载 + 📷实时检测 问题排查与修复经验

> **项目路径**: `E:\yolo\YoloDotNet-master\VisionInspectionSystem`
> **生成日期**: 2026-08-06
> **归类**: 经验文档 / 排障手册（非 P0 改造，是对已修复问题的复盘沉淀）
> **适用范围**: `🧠加载模型` 与 `📷实时检测` 两条功能链路（通用检测，不走 SOP 状态机）

---

## 〇、背景与问题全景

用户侧持续反馈了 8 类现象，最终定位到分属 3 个不同层级的问题：

```
┌────────────────────────────────────────────────────────────────┐
│  现象清单                                                        │
│                                                                  │
│  1. 切换模型后"好像都用的 yolov8s.onnx"（模型没生效？）          │
│  2. 加载 yolov8s-pose 只画框、不画骨骼关键点                     │
│  3. 加载 yolov8s-seg 看不到分割区域（掩膜）                       │
│  4. 不管什么模型，bbox 都在闪烁                                   │
│  5. 实时检测标签字体突然变小                                       │
│  6. 想调防闪烁参数，模型管理里找不到入口                          │
│  7. 选择模型时报 "String '5' was not recognized as a valid        │
│     DateTime" 异常                                               │
│  8. 调小 EMA / 调大丢失保持后，bbox 仍"原地高频闪烁"              │
│                                                                  │
│  根因分层：                                                      │
│  ├── 层A：模型加载/诊断（问题1）                                  │
│  ├── 层B：绘制层漏画（问题2、3、5）                               │
│  └── 层C：渲染架构不一致（问题4、6、7、8 中多数）                 │
└────────────────────────────────────────────────────────────────┘
```

**最重要的结论**：问题 4/8 的"bbox 闪烁"最终根因**不是平滑算法参数**，而是**实时检测与 SOP 检测使用了两套不一致的渲染架构**。SOP 丝滑、实时闪，两者共用同一个 `DetectionTrackSmoother`，所以问题不在平滑器本身，而在"谁、何时、如何把带框帧设到 `CurrentImage`"。

---

## 一、层A：模型加载诊断（问题1）

### 现象
通过 `🧠` 按钮加载模型后，`📷实时检测` 无论切换什么模型，似乎都还在用 `yolov8s.onnx`。

### 排查结论（代码流程正确，是诊断盲区 + 隐藏 bug）
正常流程本应正确：`LoadModelAsync` → `_detectionService.InitializeAsync(_loadedModel)` → `new Yolo(options)`（用选中模型 `ModelPath`）→ `DetectAsync` 用该实例推理。无覆盖/回退逻辑。

发现两个真问题：
1. **Bug**：`LoadModelAsync` 的 `else` 分支（初始化失败时）未重置 `IsModelLoaded=false`、未清 `LoadedModelName`。先加载 yolov8s 成功（`IsModelLoaded=true`），再加载其他模型失败 → UI 仍显示"yolov8s"且 `IsModelLoaded=true`，但 `_yolo` 已被置 null（`InitializeAsync` 先清再建）→ `IsInitialized=false`、推理根本不执行。
2. 错误被静默吞掉：`OnDetectionError` 只存 `_lastDetectionError`，不弹窗、不更新 Status。

### 修复
1. `LoadModelAsync` 的 else 分支补：
   ```csharp
   IsModelLoaded = false;
   LoadedModelName = "未加载模型";
   ```
   并在 Status 中带模型路径。
2. 三处加 `Debug.WriteLine` 诊断日志（前缀：`[LoadModel]` / `[YoloDetectionService]` / `[RealTimeDetection]`）：
   - `LoadModelAsync`：选中模型名/路径/文件存在性/类型/GPU/阈值。
   - `YoloDetectionService.InitializeAsync`：路径/文件存在/执行设备(CUDA/CPU)/标签数与前5标签/异常堆栈。
   - `PerformRealTimeDetectionAsync`：首帧输出已加载模型名/`IsInitialized`/实际类别数和前5类别（**类别数从 80 变 1 即证明模型真的切换了**）。
3. Status 消息带模型路径，UI 可见。

### 验证方式
VS → 视图 → 输出 → 显示输出来源选"调试"，加载模型并开实时检测，看 `[RealTimeDetection]` 首帧日志的"实际类别数"。类别数对得上所选模型即说明加载生效。

---

## 二、层B-1：Pose 模型不画骨骼（问题2）

### 现象
加载 `yolov8s-pose` 后，只画出 `person` 框，没有骨骼/关键点。诊断日志显示模型已正确切换（类别数 80→1），**所以不是加载问题，是绘制问题**。

### 根因（关键坑：DrawDetectionResults 有两个重载）
`YoloDetectionService.RunPoseEstimation` 已正确把 `r.KeyPoints` 提取到 `DetectedObject.KeyPoints`（`VisionInspection.Core.Services.KeyPoint`：Index/X/Y/Confidence）。但 `MainViewModel.DrawDetectionResults` 当时只画了框+标签，**完全没处理 `obj.KeyPoints`**。

更隐蔽的是：`MainViewModel` 里有**两个** `DrawDetectionResults` 重载：
- `DrawDetectionResults(SKBitmap, List<DetectedObject>)` —— 单图/视频路径用。
- `DrawDetectionResults(SKBitmap, DetectionResult)` —— **相机实时 `PerformRealTimeDetectionAsync` 实际调用的是这个**（它只用 `obj.BoundingBox` 画框+标签）。

**第一次修复改错重载（改了 List 版，实时走 DetectionResult 版），导致"改了不生效"。**

### 修复
1. 新增 `COCO_SKELETON`（17 点 0-indexed 骨骼连接表）+ `POSE_KEYPOINT_CONF=0.3` 常量。
2. 新增 `DrawPoseSkeleton(canvas, kps)`：先用 `Lime` 3px 线画骨骼连接（用 Index 匹配 skeleton 对，两端点置信度均≥阈值才连线），再画 `Yellow` 圆点 + `Black` 描边（半径4）。关键点坐标直接用 `KeyPoint.X/Y`（与 `PixelBoundingBox` 同坐标系）。
3. **落到真正被调用的 DetectionResult 重载**的 foreach 末尾：
   ```csharp
   if (obj.KeyPoints != null && obj.KeyPoints.Count > 0)
       DrawPoseSkeleton(canvas, obj.KeyPoints);
   ```
4. 给 `[RealTimeDetection]` 首帧诊断补关键点计数，明确打印"未检测到带关键点的对象 / 关键点数+首个点坐标"。

### 教训（重要）
> 改绘制/渲染方法前，先用 Grep 确认**所有同名重载**和**实际调用点**。`DrawDetectionResults` 双重载——`List<DetectedObject>` 版（单图/视频）和 `DetectionResult` 版（相机实时），修复必须落到真正被调用的那个。

---

## 三、层B-2：Seg 模型不画分割掩膜（问题3）

### 现象
加载 `yolov8s-seg` 后框有了，但没有看到物体的像素级区域轮廓。

### 什么是 yolov8s-seg
实例分割（Instance Segmentation）模型：检测同 80 类 COCO 物体，但额外输出**像素级掩膜**（物体精确轮廓），不只是矩形框。所以是"数据有、没画"的同类型问题。

### YoloDotNet 关键事实（本地项目，非 nuget）
本地 `../YoloDotNet/Models/Segmentation.cs`：
- `Segmentation` 类**只有** `Label/Confidence/BoundingBox/BitPackedPixelMask` 四个属性，**没有 `Mask` 这个 SKBitmap 属性**（一度误以为有 `r.Mask`，编译直接报 CS1061）。
- 掩膜是 `BitPackedPixelMask`（`byte[]`），**尺寸 = 边界框宽高（不是整图）**。位序：
  ```csharp
  byteIndex = i >> 3;
  bitIndex  = i & 7;
  isOn = (packed[byteIndex] & (1 << bitIndex)) != 0;
  ```
- 官方自带 `UnpackToBitmap(this byte[], int width, int height)` 扩展方法（在 `YoloDotNet.Extensions`，unsafe），解出白/黑掩膜再上色。

### 修复（自包含，不依赖 `YoloDotNet.Extensions`）
1. `DetectedObject.Mask` 保持 `byte[]?`（存 `r.BitPackedPixelMask`）。
2. 新增 `DrawSegmentationMask(canvas, packedMask, boxX, boxY, boxW, boxH, color)` + `unsafe UnpackSegmentationMask(packed, w, h, color)`（UI csproj 已开 `AllowUnsafeBlocks`）。解包时直接写彩色半透明像素（调色板 `SEG_MASK_COLORS` 按 `ClassId%8` 取色，alpha=110），`canvas.DrawBitmap(maskBmp, boxX, boxY)`。
3. 两个重载都加调用：List 版用 `obj.PixelBoundingBox` 的 Left/Top/Width/Height；DetectionResult 版用 `obj.BoundingBox[0..3]`。

### 教训
> 用本地 YoloDotNet 源码核实 API 再写代码，别凭记忆（`Segmentation` 没有 `Mask` 属性）；位打包掩膜尺寸是 bbox 不是整图，绘制位置要用 bbox 左上角。

---

## 四、层B-3：标签字体变小（问题5）

### 现象
实时检测改走 `DrawDetectionResults(SKBitmap, List<DetectedObject>)` 后，标签字突然变小。

### 根因
原 `List<DetectedObject>` 重载默认 `TextSize=16`、`StrokeWidth=3`；而原来实时专用（DetectionResult）重载用 `TextSize=64`、`StrokeWidth=4`。切路径后字体参数被"低配"默认值覆盖。

### 修复
`DrawDetectionResults(List<DetectedObject>)` 加可选参数 `float textSize = 24f`（默认 24）；实时检测调用时传 `32f`；`StrokeWidth` 从 3 提到 4。

---

## 五、层C-1：bbox 闪烁（问题4，第一层修复：接平滑器）

### 现象
yolov8s-seg 能画轮廓了，但**任何模型 bbox 都闪烁**。

### 根因
之前做的 `DetectionTrackSmoother`（IOU 跟踪 + EMA 平滑 + confirmHits/maxMissed）只接到了 **SOP 路径**。普通实时检测 `PerformRealTimeDetectionAsync` 在推理完成后直接 `DrawDetectionResults(bitmap, result)`（DetectionResult 重载），逐帧画原始框 → 闪。

### 修复
1. **扩展 `DetectionTrackSmoother` 支持 `DetectedObject`**（原只支持 `ObjectDetection`）：
   - `Track` 加 `Raw`(object) 与 `SmoothedKeyPoints`。
   - 抽 `UpdateInternal(...)`；新增 `Update(List<DetectedObject>)`（从 `PixelBoundingBox` 转 SKRect），保留 `Update(List<ObjectDetection>)`（`BoundingBox` 是 SKRectI，需转 SKRect 再喂 `UpdateInternal`）。
   - 新增 `GetActiveObjects()`：返回平滑后的 `List<DetectedObject>`，保留 ClassId/ClassName/Mask/KeyPoints(用 SmoothedKeyPoints)/IsInRoi/RoiId，坐标写回 `BoundingBox[4]` 与 `PixelBoundingBox`。
   - 注意 `KeyPoint` 在 `YoloDotNet.Models` 和 `VisionInspection.Core.Services` 都存在 → 文件头加 `using KeyPoint = VisionInspection.Core.Services.KeyPoint;` 消除歧义。
2. `MainViewModel` 加独立 `_realtimeSmoother`（与 SOP 的 `_detectionSmoother` 隔离，避免状态互相污染）。
3. 实时渲染块改为：`_realtimeSmoother.Update(result.Objects); var smoothed = _realtimeSmoother.GetActiveObjects(); DetectionResults = smoothed; DrawDetectionResults(bitmap, smoothed);`（走已支持 pose+seg 的 List 重载）。
4. `StopRealTimeDetection()` 加 `_realtimeSmoother.Clear();`。

### 注
视频路径与单图路径：视频路径（当时仍走原始 List）未接 smoother，如需可同样按 `_realtimeSmoother.Update/GetActiveObjects` 接（需独立 smoother 实例）；单图为静态单帧，无需平滑。

---

## 六、层C-2：防闪烁参数开放到模型管理（问题6）

### 需求
用户希望模型管理里可调闪烁参数，并询问 IoU 含义。

### IoU 解释
Intersection over Union，衡量两个候选框重叠度。模型管理里原有的 IoU 阈值用于 **NMS（非极大值抑制）去重**，与"闪烁"无直接关系。本修复新增的是另一组**跟踪匹配 IoU**（相邻帧目标匹配用），两者语义不同，不要混淆。

### 开放的 4 个实时显示平滑参数
| 参数 | 含义 | 范围 | 默认 | 调参建议 |
|------|------|------|------|---------|
| `SmoothEma` | EMA 平滑系数（新框位置权重） | 0.05~0.5 | 0.2 | 越小越稳但越滞后；想跟手就调大 |
| `SmoothConfirmHits` | 新目标连续命中帧数才显示 | 1~10 | 2 | 调大可过滤瞬态误检 |
| `SmoothMaxMissed` | 目标丢失后保持显示帧数 | 0~15 | 5 | 调大可避免短暂漏检闪退 |
| `SmoothIouThreshold` | 相邻帧目标跟踪匹配 IoU 阈值 | 0.05~0.8 | 0.3 | 抖动场景太低会失配，可适度调低（更宽松匹配） |

### 改动文件
- `ModelInfo`：增加上述 4 个属性。
- `ModelManager`：
  - `InitializeDatabase` 新表结构含 4 列；`EnsureColumnExists` 兼容旧库自动加列。
  - `GetAllModelsAsync` / `GetModelAsync` / `AddModelAsync` / `UpdateModelAsync` / `ImportScannedModelsAsync` 全部读写新字段。
- `ModelManagerWindow.xaml`：在 IoU 阈值下方新增 GroupBox「实时显示平滑（防闪烁）」，4 个滑块 + 4 个数值标签。
- `ModelManagerWindow.xaml.cs`：绑定滑块事件、DisplayModelDetails、Add/Update/ClearForm 同步。
- `MainViewModel.LoadModelAsync`：模型初始化成功后，用 `_loadedModel` 的 4 个参数重建 `_realtimeSmoother`（字段非 readonly，可重新赋值）；日志输出参数值。

---

## 七、层C-3：选择模型报 DateTime 解析异常（问题7）

### 现象
在模型管理里选择模型时报：`String '5' was not recognized as a valid DateTime`。

### 根因
SQLite `ALTER TABLE ADD COLUMN` 会把新列追加到表**末尾**，改变物理列顺序。而 `GetAllModelsAsync` / `GetModelAsync` 之前用**硬编码列索引**读取。旧数据库升级后列顺序与 `CREATE TABLE` 不一致，导致把 `SmoothMaxMissed=5` 当成了 `CreatedAt`，触发 DateTime 解析异常。

### 修复
将 `ModelManager` 中两个读取方法全部改为按**列名**读取：
```csharp
var col = reader.GetOrdinal("SmoothMaxMissed");
if (!reader.IsDBNull(col)) model.SmoothMaxMissed = reader.GetInt32(col);
```
不再依赖任何列顺序假设。

### 教训
> 数据库读取**务必用列名，绝不用硬编码索引**；`ALTER TABLE` 追加列会改变物理顺序，旧库升级后是头号隐患。

---

## 八、层C-4：原地高频闪烁（问题8，算法层根因）

### 现象
用户按推荐参数（EMA=0.10 等）仍闪。排查确认**参数已正确传入** smoother（`LoadModelAsync` 重建用的是 `_loadedModel` 的 4 个参数，字段非 readonly）。说明问题在**匹配/显示算法**，不是参数值。

### 真正的两个算法缺陷（`DetectionTrackSmoother`）
1. **跟踪匹配只认纯 IoU > 阈值（默认 0.30）**：框原地抖动时相邻帧 IoU 常掉到 0.30 以下 → 匹配失配 → 每帧重建轨道，EMA 永不积累，框跳。
2. **显示条件依赖 `HitCount >= confirmHits` + 失配时 `HitCount=0` 重置**：已显示的框一旦偶发失配（抖动），`HitCount` 被清零 → 当帧不显示 → 下一帧匹配上又显示 → 框"忽隐忽现/高频闪烁"。

### 修复
1. 新增 `MatchScore`：综合 **IoU + 中心点归一化距离 + 尺寸相似度**。抖动场景（中心点基本不动、尺寸不变）也能稳定匹配上。原 `_iouThreshold` 参数现作为"综合匹配分阈值"语义保留。
2. `Track` 增加 `EverConfirmed` 标志：连续命中 `confirmHits` 帧后置 `true`；`GetActiveBoxes`/`GetActiveObjects` 显示条件改为 `EverConfirmed`（而非当前 `HitCount>=confirmHits`）；失配分支**不再重置 `HitCount`**，已确认轨道只在 `MissedCount > maxMissed` 时才删除 → 偶发失配框不再消失。

### 实现注意
`MathF.Clamp` 在此 TFM 不可用，改用 `Math.Clamp`。

### 教训
> bbox 时序平滑的"匹配"不能只靠 IoU；抖动场景必须叠加中心点距离/尺寸相似兜底，否则平滑器形同虚设。显示条件要区分"首次确认"与"续显"，失配不应立即隐藏已确认目标。

---

## 九、层C-5：仍闪（最终根因——渲染架构不一致）

### 现象
smoother 算法改动后仍闪，但 **SOP 检测 bbox 非常丝滑**——两者共用同一个 `DetectionTrackSmoother` 类，说明问题在**调用/渲染方式**，而非平滑器。

### 真正的根因（渲染架构不一致）
- `OnCameraImageGrabbed`（相机帧回调，始终注册）**每帧**都先 `RoiEditorViewModel.CurrentImage = 原始帧(skBitmap)`（**无框**）。
- 实时检测 `PerformRealTimeDetectionAsync` 在推理**完成后**才 `CurrentImage = 带框帧`。
- 相机帧率(如30fps) >> 实时推理频率(如10fps)，导致屏幕在「无框原始帧」与「有框帧」之间高频交替 = 用户看到的"bbox 闪烁/时有时无"。
- **SOP 丝滑原因**：SOP 在 `OnCameraImageGrabbed` 里**直接把 `_lastSopResult` 叠加到最新相机帧再设 `CurrentImage`**，每帧都有框，且 `_lastSopResult` 是 `_detectionSmoother` 平滑后的，配合老化机制 → 丝滑。

### 修复（让实时检测复用 SOP 渲染架构）
1. 提取 `DrawDetectedObjects(SKCanvas, List<DetectedObject>, float textSize)` 绘制核心（从 `DrawDetectionResults` 抽出），供叠加复用。
2. 新增字段 `_lastRealtimeObjects`（缓存最近平滑结果）、`_realtimeObjectAge` / 常量 `REALTIME_CACHE_MAX_AGE=15`（老化）。
3. `PerformRealTimeDetectionAsync`：**不再**在推理后设置 `CurrentImage`，改为把 `smoothed` 写入 `_lastRealtimeObjects` 并重置老化计数（缓存）。
4. `OnCameraImageGrabbed`：SOP 块之后，若 `IsRealTimeDetecting && _lastRealtimeObjects != null`，在 skBitmap 上 `DrawDetectedObjects` 叠加，并做老化计数（超过 15 帧无新结果清空缓存）。
5. `StopRealTimeDetection`：清空 `_lastRealtimeObjects` 与老化计数。

### 教训（最关键的一条）
> WPF 相机实时叠加必须"**每帧在最新帧上叠加缓存结果后统一设 `CurrentImage`**"，绝不能"先设无框原始帧、推理完再设带框帧"——后者在帧率不匹配时必然闪烁。SOP 与实时检测应共用同一渲染管线（缓存 + 每帧叠加 + 老化）。

---

## 十、修改文件清单总览

| 文件 | 关联问题 | 改动 |
|------|---------|------|
| `src/UI/VisionInspection.UI/ViewModels/MainViewModel.cs` | 1,2,3,4,5,6,8,9 | 诊断日志；`DrawDetectionResults` 两重载加 `DrawPoseSkeleton`/`DrawSegmentationMask`；`DrawDetectedObjects` 抽取；`_lastRealtimeObjects` 缓存+老化；`_realtimeSmoother` 按参重建；`OnCameraImageGrabbed` 每帧叠加；`LoadModelAsync` 失败分支重置 |
| `src/Modules/VisionInspection.Modules.Detection/YoloDetectionService.cs` | 2,3 | `RunPoseEstimation` 提取 KeyPoints；`RunSegmentation` 存 `BitPackedPixelMask` 到 `DetectedObject.Mask`；初始化诊断日志 |
| `src/Core/VisionInspection.Core/Services/IDetectionService.cs` | 3,6 | `DetectedObject.Mask` 保持 `byte[]?`；`ModelInfo` 增 4 个平滑参数 |
| `src/UI/VisionInspection.UI/Services/DetectionTrackSmoother.cs` | 4,8 | 扩展支持 `DetectedObject`；`MatchScore` 综合匹配；`EverConfirmed` 续显；保留 KeyPoints/Mask |
| `src/UI/VisionInspection.UI/Services/ModelManager.cs` | 6,7 | DB 加 4 列+自动迁移；读取改**按列名**；CRUD 含新字段 |
| `src/UI/VisionInspection.UI/Views/ModelManagerWindow.xaml` + `.xaml.cs` | 6 | 「实时显示平滑（防闪烁）」分组：4 滑块+4 数值标签；绑定/加载/保存同步 |

---

## 十一、排障决策树（速查）

```
实时检测出问题
│
├─ 模型像没切换（类别不对 / 行为不符）
│   └─ 看 [RealTimeDetection] 首帧日志"实际类别数"；检查 LoadModelAsync else 分支是否重置 IsModelLoaded
│
├─ 框有但缺信息（无骨骼 / 无掩膜 / 字小）
│   └─ 核对 DrawDetectionResults 的【两个重载】是否都改到；
│       pose→DrawPoseSkeleton；seg→BitPackedPixelMask 解包（尺寸=bbox）；字→textSize 参数
│
├─ 选择模型直接报错（DateTime 等）
│   └─ 查 ModelManager 读取是否用了硬编码列索引 → 全部改按列名读取
│
└─ bbox 闪烁
    ├─ 第一步：接 _realtimeSmoother（Update/GetActiveObjects）
    ├─ 第二步：检查参数是否真传入（LoadModelAsync 重建 smoother）
    ├─ 第三步：算法层 MatchScore + EverConfirmed（抖动兜底）
    └─ 第四步（最终）：渲染架构！OnCameraImageGrabbed 每帧叠加缓存，
       绝不在推理后才设带框帧（与 SOP 同管线）
```

---

## 十二、编译验证

```bash
cd E:\yolo\YoloDotNet-master\VisionInspectionSystem
dotnet build
```

所有改动合并后编译 0 错误（仅有既定的 SKPaint 过时警告，无关）。

### 全局搜索验证点
```
搜索 "DrawPoseSkeleton"        → MainViewModel.cs（定义+两重载调用）
搜索 "DrawSegmentationMask"    → MainViewModel.cs（定义+两重载调用）
搜索 "MatchScore"              → DetectionTrackSmoother.cs
搜索 "EverConfirmed"           → DetectionTrackSmoother.cs
搜索 "_lastRealtimeObjects"    → MainViewModel.cs（缓存+叠加+Stop清空）
搜索 "SmoothEma"               → ModelInfo + ModelManager + ModelManagerWindow + MainViewModel
搜索 "GetOrdinal"              → ModelManager.cs（按列名读取）
```
