# SOP 配方 YAML 编写指引

本指引说明如何编写 SOP（标准作业流程）检测配方 YAML 文件。系统采用**配方驱动 + 规则数据驱动**架构：引擎（状态机 / 条件评估 / 违规检测）是通用的，每个产品只是一个 YAML，声明自己的区域、步骤、必放物料。**切换产品 = 加载另一个 YAML，无需改代码。**

---

## 1. 文件位置与加载

| 项 | 说明 |
|---|---|
| 目录 | `configs/sop/` |
| 命名 | 建议 `sop_产品名.yaml`（如 `sop_phone_packaging_demo.yaml`）；范本见 `sop_template.yaml` |
| 扫描 | 界面「📦 产品配方」下拉框启动时自动扫描该目录，列出全部 `*.yaml/*.yml` |
| 解析 | 用 YamlDotNet 反序列化为 `SOPYamlConfig` → 映射为内部 `SOPWorkflow` |

> 提示：当前目录已有 `sop_template.yaml`（全注释范本），可直接复制改名使用；它也会出现在下拉框里作为参考。

---

## 2. 根结构一览

```yaml
sop:
  name: "产品名称"            # 下拉框显示名（必填）
  version: "1.0"
  description: "说明"
  settings: { ... }           # 全局检测设置
  regions: { ... }            # ROI 区域（坐标像素）
  steps:                      # 作业步骤（有序列表）
    - id: "1"
      name: "步骤名"
      detection: { method: "..." }
      required_objects: [ ... ]   # 可选：漏放校验
      forbidden_objects: [ ... ]  # 可选：禁现类别
  model: { ... }              # 检测模型配置
```

---

## 3. `settings`（全局设置）

| 字段 | 类型 | 说明 |
|---|---|---|
| `detectionMode` | string | 固定 `"UnifiedDetection"`（YOLO 物体 + DWPose 手部） |
| `confidenceThreshold` | float | 检测置信度阈值 (0–1) |
| `stableFrames` | int | 状态稳定所需连续帧数 |
| `timeoutSeconds` | int | 单步默认超时（秒） |
| `enableSkipDetection` | bool | 是否检测“跳步” |
| `enableTimeoutDetection` | bool | 是否检测“超时” |

---

## 4. `regions`（ROI 区域）

区域坐标 `(x1,y1,x2,y2)` 是**相对相机帧分辨率**的像素，必须用「🎯 标定区域」工具实际拖框得到（占位值仅用于示例）。区域 ID（如 `part_box`）会在步骤的 `detection.region` 中被引用。

```yaml
regions:
  part_box: { x1: 50, y1: 200, x2: 250, y2: 400, name: "料盒" }
  finished: { x1: 600, y1: 300, x2: 800, y2: 500, name: "成品区" }
```

> 可用「🎯 标定区域」工具：拖框 → 改名（名称即区域 ID）→ 保存，自动写回本文件的 `regions`。

---

## 5. `steps`（作业步骤）

每个步骤用一个 `detection` 描述“如何算完成”。支持的 `method`：

| method | 含义 | 关键字段 |
|---|---|---|
| `hand_in_region` | 手进入某区域 | `hand`(right/left), `region` |
| `hand_not_in_region` | 手离开某区域 | `hand`, `region` |
| `hand_stable` | 手在某处稳定不动 | `hand`, `tolerance` |
| `hand_move` | 手从 A 区移到 B 区 | `hand`, `from_region`, `to_region` |
| `hand_near_object` | 手靠近某物体 | `hand`, `target_object`, `tolerance` |
| `object_present` | 画面出现某物体 | `target_object`, `min_confidence` |
| `object_in_zone` | 某物体在某区域内 | `target_object`, `region` |
| `pose_stable` | 物体姿态稳定 | `tolerance` |
| `time_elapsed` | 停留计时（常用于最终校验步） | `stable_frames`(≈秒) |
| `person_present` | 画面出现人员（COCO 通用模型即支持 `person`，无需专用模型） | `min_confidence` |
| `hand_action` | 手部"取/放"语义：`action: pickup` = 手曾位于 `from_region` 且已离开；`action: putdown` = 手到达 `to_region`。强烈建议配 `target_object` 做辅助判定，避免历史记录缺失导致卡死 | `action`(pickup/putdown), `hand`(any/right/left), `from_region`/`to_region`, `target_object`(可选，如 `cell phone`) |

> ⚠️ `detection.method` 必须是上表中的值。**未识别的方法会直接报错（加载配方失败），而不会像以前那样被静默忽略、导致步骤无条件下通过。**

示例：

```yaml
- id: "1"
  name: "取料"
  timeout: 15
  detection:
    method: "hand_in_region"
    hand: "right"
    region: "part_box"
```

`hand_action` 示例（拿起手机）：

```yaml
- id: "2"
  name: "拿起手机"
  timeout: 5
  detection:
    method: "hand_action"
    action: "pickup"
    hand: "any"
    from_region: "phone_table"
    target_object: "cell phone"   # 辅助判定：手拿着 cell phone 离开 phone_table 即视为拿起
    min_confidence: 0.6
    stable_frames: 3
```

---

## 6. 违规 / 校验规则（可选）

### 6.1 漏放校验 `required_objects`（重点，跨产品复用）

在**成品校验步**声明“必须存在的物料”，任意一项缺失即判漏放。这是通用规则，**每个配方声明自己的物料即可跨产品复用**。

```yaml
- id: "3"
  name: "成品校验（漏放检测）"
  detection:
    method: "time_elapsed"
    stable_frames: 2           # 停留约 2 秒，让成品稳定可见
  required_objects:
    - object: "phone"    zone: "finished"  min_confidence: 0.5
    - object: "charger"  zone: ""          min_confidence: 0.5  # zone 留空=全局
```

字段：`object`(类别名) / `zone`(可选区域 ID，限定该物料必须出现在某区) / `min_confidence`(默认 0.5)。

> 防误报：当画面完全没有任何必放物料时（成品尚未入框），不判漏放。

### 6.2 禁现类别 `forbidden_objects`

本步不应出现的类别（如错料）：

```yaml
forbidden_objects: ["wrongPart", "scrap"]
```

### 6.3 必保持 `must_keep`

本步必须持续存在的类别（防零件被中途移除）：

```yaml
must_keep: ["part_base", "part_cover"]
```

---

## 7. `model`（模型配置）

| 字段 | 说明 |
|---|---|
| `path` | ONNX 模型路径（相对 `yolo_models/` 或绝对路径） |
| `type` | `"ObjectDetection"` |
| `confidence` / `iou` | 检测阈值 / NMS IoU |
| `useGpu` | 是否用 GPU |
| `classes` | 模型可识别的类别名列表（**必须与训练时一致**） |

```yaml
model:
  path: "yolo_models/sop_phone_yolo26s.onnx"
  type: "ObjectDetection"
  confidence: 0.5
  iou: 0.45
  useGpu: true
  classes: ["phone", "case_top", "case_bottom", "manual", "charger", "cable"]
```

> ⚠️ 当前仓库仅有通用 COCO 模型（`yolov8s.onnx`），认不出手机/盒/配件等专属形态。务必训练专用模型并替换 `path`，否则 `object_present` / `hand_near_object` / 漏放校验都会持续误报。

---

## 8. 最小可用范本

```yaml
sop:
  name: "最小配方"
  settings:
    detectionMode: "UnifiedDetection"
    confidenceThreshold: 0.5
    stableFrames: 3
    timeoutSeconds: 30
    enableSkipDetection: true
    enableTimeoutDetection: true
  regions: {}
  steps:
    - id: "1"
      name: "步骤1"
      timeout: 30
      detection: { method: "time_elapsed", stable_frames: 2 }
    - id: "2"
      name: "成品校验（漏放检测）"
      timeout: 30
      detection: { method: "time_elapsed", stable_frames: 2 }
      required_objects: []
  model:
    path: "yolo_models/sop_custom_yolov8s.onnx"
    type: "ObjectDetection"
    confidence: 0.5
    iou: 0.45
    useGpu: true
    classes: []
```

> 更完整的带注释范本见 `configs/sop/sop_template.yaml`。

---

## 9. 常见场景示例

### 场景 A：手部取放（手机包装）
取料（手进 `part_box`）→ 放料（手从 `part_box` 移到 `fixture`）→ 压合（手靠近 `phone`）→ 成品漏放校验。

### 场景 B：漏放检测（换产品只改物料）
把 `required_objects` 的 `phone/case_top/...` 换成另一产品的物料集合即可，引擎无需改动——这就是“规则跨配方复用”。

### 场景 C：防错料
在关键步骤加 `forbidden_objects: ["wrongPart"]`，出现错料即违规。

---

## 10. 与界面按钮的对应关系

| 操作 | 界面入口 | 作用 |
|---|---|---|
| 切换产品 | 「📦 产品配方」下拉框 | 加载另一 YAML，运行/标定同步生效 |
| 新建 | 「➕ 新建」 | 弹窗填名称+选模板，生成 `sop_xxx.yaml` 并自动选中 |
| 重命名 | 「✏️ 重命名」 | 仅改 YAML 内 `sop.name`（文件名不变） |
| 删除 | 「🗑 删除」 | 确认后删除该 YAML 文件（不可恢复） |
| 标定区域 | 「🎯 标定区域」 | 拖框定义/修改 `regions` 并写回 |

---

## 11. 注意事项

1. 区域坐标是相机像素，相机分辨率变更后需重新标定。
2. `classes` 必须与训练模型的类别名**逐字一致**，否则命中不了。
3. 漏放校验建议放在最后一步并停留约 2 秒（`time_elapsed` + `stable_frames: 2`）。
4. YAML 缩进用空格；区域可用 `{ x1:.., y1:.., name:.. }` 单行写法。
5. 部署时替换 `VisionInspection.UI.dll`（及 SOP 模块相关 dll）。
