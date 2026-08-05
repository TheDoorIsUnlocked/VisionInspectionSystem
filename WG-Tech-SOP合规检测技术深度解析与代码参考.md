# WG Tech Solutions SOP 合规性检测 — 技术深度解析与代码参考

> 调研日期：2026-07-28 | 来源：Ultralytics 官方案例、Axelera 社区、Voyager SDK GitHub、Ultralytics 文档、WG Tech 官网

---

## 一、案例全景：从业务到工程

### 1.1 案例一句话总结

WG Tech Solutions（印度班加罗尔）为某领先 ODM 的多工厂环境构建了 **WGDeepInsight** 平台，使用 **Ultralytics YOLO11 + YOLOv8**（约 45 个专用模型）运行在 **Axelera Metis** 边缘 AI 加速器上，将工人安全违规行为减少 **28%**，实现了 SOP 合规性的实时自动化监控。

### 1.2 核心数据

| 维度 | 数值 |
|------|------|
| 专用模型数量 | ~45 个 |
| 安全违规减少 | 28% |
| 单流推理速度 | 25-30 FPS（内部验证） |
| 数据采集周期 | 3 周 |
| 使用的 YOLO 版本 | YOLO11（新用例）+ YOLOv8（已有管线） |
| 硬件形态 | Metis M.2 / PCIe / Compute Board（混合部署） |
| 告警端到端延迟 | 检测 + 规则验证后即刻触发 |

---

## 二、系统架构详解

### 2.1 整体架构（来自 Axelera 社区技术深度解析）

```
┌─────────────────────────────────────────────────────────────────────┐
│                        WGDeepInsight 平台                           │
│                                                                     │
│  ┌──────────┐   ┌──────────────────┐   ┌──────────────────────┐   │
│  │  摄像头   │──▶│  Voyager SDK     │──▶│  规则引擎             │   │
│  │ (多工位)  │   │  (边缘推理)       │   │  (Rule-based Alert)  │   │
│  └──────────┘   │                  │   └──────┬───────────────┘   │
│                 │  • 解码视频流      │          │                   │
│                 │  • 运行 YOLO 模型  │          ├─▶ 仪表板 (Dashboard)│
│                 │  • 输出标注与事件  │          ├─▶ 邮件通知          │
│                 │                  │          ├─▶ 消息系统          │
│                 │  45个模型按需调用  │          └─▶ 存储归档          │
│                 └──────────────────┘                             │
│                                                                     │
│  DeepInsight 外层负责：                                              │
│  • 多摄像头输入管理       • 模型生命周期管理                          │
│  • 多模型编排             • 告警路由与分发                            │
│  • 违规规则判定           • 企业集成 (ERP/MES)                        │
└─────────────────────────────────────────────────────────────────────┘
```

### 2.2 关键架构决策

#### 决策一：45 个专用模型 vs 1 个通用模型

```
❌ 通用模型方案（被否决）
   1 个大模型 → 所有工位所有任务 → 精度低、误报高

✅ WG Tech 实际方案
   45 个专用模型 → 每个针对特定工位/任务/相机角度 → 高精度、低误报
```

**45 个模型的分类**：

| 模型类别 | 用途 | 典型数量 |
|---------|------|---------|
| PPE 检测 | 头盔、背心、手套合规 | ~5-8 |
| 安全区域监控 | 禁区闯入、危险区域 | ~4-6 |
| 过程验证 | SOP 步骤顺序与动作 | ~8-10 |
| 缺件检测 | 装配缺失零件 | ~5-7 |
| 缺陷检测 | 产品表面/结构缺陷 | ~5-8 |
| 人员监控 | 在场、密度、追踪 | ~4-6 |
| 安防分析 | 未授权访问 | ~3-5 |

**设计理念**（原文引用）：

> "Every manufacturing station presents unique lighting conditions, camera angles, object sizes, background clutter, and operational workflows. Rather than forcing one generic model to solve every problem, WG Tech develops highly specialised models tuned for individual production processes, resulting in significantly higher accuracy and fewer false alarms."

#### 决策二：混合硬件部署

| 硬件 | 部署位置 | 计算性能 (INT8) | 适用场景 |
|------|---------|----------------|---------|
| Metis M.2 | 工作站旁紧凑边缘盒 | 214 TOPS | 轻量推理 |
| Metis PCIe | PC 级工作站 | 214-856 TOPS | 高性能多路 |
| Metis Compute Board | 独立计算节点 | 214 TOPS | 多路视频流 |

> 同一 Voyager 管线在三种硬件上运行，**无需重写推理层**。

#### 决策三：YOLO11 + YOLOv8 双版本策略

```
新用例 ──▶ YOLO11（追求更高精度和性能）
         │
已有管线 ──▶ YOLOv8（已验证、已部署，"能用就不动"）
```

> 原则："if it works, don't fix it"

---

## 三、SOP 合规性检测场景详解

### 3.1 八大检测场景

#### 场景 1：SOP 步骤顺序验证

```
工位摄像头 → YOLO 检测当前动作 → 规则引擎验证步骤顺序
                                        │
                                        ├─ 步骤正确 → 继续
                                        ├─ 步骤遗漏 → 告警
                                        └─ 步骤顺序错误 → 告警
```

**规则引擎伪代码**：

```python
class SOPStepValidator:
    """SOP 步骤顺序验证器 — 基于 WG Tech 案例设计的参考实现"""
    
    def __init__(self, expected_steps: list[str]):
        self.expected_steps = expected_steps
        self.current_step_index = 0
        self.step_history = []
    
    def validate(self, detected_action: str, timestamp: float) -> dict:
        expected = self.expected_steps[self.current_step_index]
        
        if detected_action == expected:
            self.step_history.append({
                "step": detected_action,
                "timestamp": timestamp,
                "status": "correct",
                "step_index": self.current_step_index
            })
            self.current_step_index += 1
            return {"status": "ok", "message": f"Step {self.current_step_index} completed"}
        
        # 检查是否跳过了步骤
        if detected_action in self.expected_steps[self.current_step_index + 1:]:
            skipped = self.expected_steps[self.current_step_index:self.expected_steps.index(detected_action)]
            self.step_history.append({
                "step": detected_action,
                "timestamp": timestamp,
                "status": "skipped_steps",
                "skipped": skipped
            })
            return {"status": "violation", "message": f"Skipped steps: {skipped}"}
        
        # 步骤顺序错误
        self.step_history.append({
            "step": detected_action,
            "timestamp": timestamp,
            "status": "wrong_order",
            "expected": expected,
            "actual": detected_action
        })
        return {"status": "violation", "message": f"Expected '{expected}', got '{detected_action}'"}
```

#### 场景 2：托盘处理工作流

```python
class PalletHandlingValidator:
    """托盘处理验证器 — 验证拾放正确性和操作顺序"""
    
    def __init__(self):
        self.required_sequence = [
            "approach_pallet",
            "pick_item",
            "transport_item", 
            "place_item",
            "release_item",
            "withdraw_hand"
        ]
        self.current_step = 0
        self.violations = []
    
    def check_pick_place(self, detection_result, hand_pose):
        """
        检测内容：
        1. 物品拾取是否正确（双手 vs 单手）
        2. 放置位置是否正确
        3. 步骤顺序是否正确
        """
        # 单手托盘处理检测（WG Tech 案例中明确提到的场景）
        if detection_result.action == "pick" and hand_pose.hand_count == 1:
            self.violations.append({
                "type": "single_hand_pallet_handling",
                "severity": "warning",
                "message": "单手托盘处理检测到，建议双手操作"
            })
        
        # 步骤顺序验证
        if detection_result.action != self.required_sequence[self.current_step]:
            self.violations.append({
                "type": "step_order_violation",
                "expected": self.required_sequence[self.current_step],
                "actual": detection_result.action,
                "severity": "error"
            })
        else:
            self.current_step = (self.current_step + 1) % len(self.required_sequence)
        
        return self.violations
```

#### 场景 3：PPE 合规监控

```python
class PPEComplianceChecker:
    """PPE 合规检查器"""
    
    REQUIRED_PPE = {
        "helmet": {"min_confidence": 0.7, "required": True},
        "vest": {"min_confidence": 0.7, "required": True},
        "gloves": {"min_confidence": 0.6, "required": True},
        "safety_glasses": {"min_confidence": 0.6, "required": False},
    }
    
    def check_compliance(self, detections: list) -> list:
        """
        输入：YOLO 检测结果列表
        输出：违规列表
        """
        violations = []
        detected_ppe = {}
        
        for det in detections:
            if det["class"] in self.REQUIRED_PPE and det["confidence"] >= self.REQUIRED_PPE[det["class"]]["min_confidence"]:
                detected_ppe[det["class"]] = True
        
        for ppe_type, config in self.REQUIRED_PPE.items():
            if config["required"] and ppe_type not in detected_ppe:
                violations.append({
                    "type": "ppe_missing",
                    "item": ppe_type,
                    "severity": "critical",
                    "message": f"缺少必要 PPE: {ppe_type}",
                    "timestamp": time.time()
                })
        
        return violations
```

#### 场景 4：CCTV 监控室人员追踪

```python
class PersonnelTracker:
    """人员配置阈值监控"""
    
    def __init__(self, min_personnel: int = 2, max_personnel: int = 5):
        self.min_personnel = min_personnel
        self.max_personnel = max_personnel
    
    def check_staffing(self, tracked_persons: list) -> dict:
        count = len(tracked_persons)
        
        if count < self.min_personnel:
            return {
                "status": "alert",
                "type": "understaffed",
                "current": count,
                "required": self.min_personnel,
                "message": f"人员配置不足: {count}/{self.min_personnel}"
            }
        elif count > self.max_personnel:
            return {
                "status": "alert", 
                "type": "overcrowded",
                "current": count,
                "max": self.max_personnel,
                "message": f"过度拥挤: {count}/{self.max_personnel}"
            }
        
        return {"status": "ok", "count": count}
```

#### 场景 5：质量检查工作流

```python
class QualityInspectionValidator:
    """质量检查工作流验证器"""
    
    def __init__(self):
        self.required_tools = {
            "torque_wrench": "扭矩扳手",
            "multimeter": "万用表", 
            "magnifier": "放大镜"
        }
        self.step_time_limits = {
            "visual_check": 30,      # 秒
            "torque_check": 45,
            "electrical_test": 60,
            "final_inspection": 20
        }
    
    def validate_workflow(self, detection, step_name, elapsed_time):
        violations = []
        
        # 验证指定工具使用
        if step_name in self.required_tools:
            required_tool = self.required_tools[step_name]
            if not self._tool_in_use(detection, required_tool):
                violations.append({
                    "type": "wrong_tool",
                    "expected": required_tool,
                    "severity": "warning",
                    "message": f"应使用 {required_tool}，检测到其他工具或无工具"
                })
        
        # 验证任务时间
        if step_name in self.step_time_limits:
            time_limit = self.step_time_limits[step_name]
            if elapsed_time > time_limit * 1.5:
                violations.append({
                    "type": "time_exceeded",
                    "step": step_name,
                    "elapsed": elapsed_time,
                    "limit": time_limit,
                    "severity": "warning",
                    "message": f"步骤耗时过长: {elapsed_time:.1f}s (限制 {time_limit}s)"
                })
            elif elapsed_time < time_limit * 0.3:
                violations.append({
                    "type": "too_fast",
                    "step": step_name,
                    "elapsed": elapsed_time,
                    "severity": "warning",
                    "message": f"步骤执行过快: {elapsed_time:.1f}s (建议 ≥{time_limit*0.3:.0f}s)"
                })
        
        return violations
    
    def _tool_in_use(self, detection, tool_name):
        """检查检测结果中是否包含指定工具"""
        for det in detection:
            if det["class"] == tool_name and det["confidence"] > 0.6:
                return True
        return False
```

### 3.2 告警分发系统

```python
class AlertRouter:
    """多通道告警路由 — 基于 WG Tech 案例的告警机制"""
    
    def __init__(self):
        self.channels = {
            "email": EmailNotifier(),
            "message": MessageSystemNotifier(),
            "dashboard": DashboardNotifier(),
        }
        self.role_rules = {
            "floor_manager": ["critical", "error"],
            "safety_officer": ["critical"],
            "shift_supervisor": ["error", "warning"],
            "operator": ["warning"],
        }
    
    def route_alert(self, violation: dict, recipients: list[dict]):
        """
        根据违规严重程度和接收者角色路由告警
        """
        severity = violation.get("severity", "warning")
        
        for recipient in recipients:
            role = recipient["role"]
            notify_levels = self.role_rules.get(role, [])
            
            if severity in notify_levels:
                alert = {
                    **violation,
                    "recipient": recipient["name"],
                    "role": role,
                    "timestamp": time.time(),
                    "channel": []
                }
                
                # 发送到所有适用通道
                for channel_name, notifier in self.channels.items():
                    try:
                        notifier.send(recipient, alert)
                        alert["channel"].append(channel_name)
                    except Exception as e:
                        print(f"Failed to send via {channel_name}: {e}")
                
                print(f"Alert sent to {recipient['name']} ({role}): {violation['message']}")
```

---

## 四、Voyager SDK 推理管线代码

### 4.1 管线构建器 API（来自 Voyager SDK GitHub）

Voyager SDK 提供了 Python 原生的管线构建器，以下是 WG Tech 架构中推理层的核心代码模式：

```python
from axelera.runtime import op

# ============================================================
# 基础 YOLO 检测管线（单模型）
# ============================================================
detection_pipeline = op.seq(
    op.color_convert("RGB", src="BGR"),       # OpenCV 读取 BGR，模型需要 RGB
    op.letterbox(640, 640),                    # 调整尺寸
    op.to_tensor(),                            # 转为张量
    op.load("ppe_detection_model.axm"),        # 加载编译后的 YOLO 模型
    ConfidenceFilter(threshold=0.25),          # 自定义操作符：置信度过滤
    op.to_image_space(),                       # 坐标归一化到图像空间
).optimized()  # 运行时自动融合操作符以最大化吞吐量

# ============================================================
# 级联管线：检测 → 跟踪（WG Tech 用于人员追踪）
# ============================================================
tracking_pipeline = op.seq(
    op.color_convert("RGB", src="BGR"),
    op.letterbox(640, 640),
    op.to_tensor(),
    op.load("person_detection_model.axm"),
    op.ax_detection(),                         # 原始输出 → DetectedObject 列表
    op.tracker(algo="tracktrack"),             # 多目标跟踪
).optimized()

# ============================================================
# 姿态估计 + 跟踪管线（用于工人安全姿态分析）
# ============================================================
pose_tracking_pipeline = op.seq(
    op.color_convert("RGB", src="BGR"),
    op.letterbox(640, 640),
    op.to_tensor(),
    op.load("yolo11n-pose.axm"),               # YOLO11 姿态估计模型
    ConfidenceFilter(threshold=0.25),
    op.to_image_space(keypoint_cols=range(6, 57, 3)),  # 关键点坐标归一化
    op.ax_pose(num_keypoints=17, class_id_type=op.CocoClasses),  # → PoseObject 列表
    op.tracker(algo="tracktrack"),             # → TrackedObject 列表
).optimized()
```

### 4.2 自定义操作符（关键能力）

```python
class ConfidenceFilter(op.Operator):
    """
    自定义置信度过滤操作符。
    与内置操作符一样被融合、调度和优化，无额外开销。
    """
    threshold: float = 0.25
    score_col: int = 4

    def __call__(self, x: np.ndarray) -> np.ndarray:
        """过滤低置信度检测结果"""
        if x.ndim == 3:
            x = x[0]
        return x[x[:, self.score_col] >= self.threshold]


class SOPStepDetector(op.Operator):
    """
    自定义 SOP 步骤检测操作符 — 基于 WG Tech 案例设计。
    在管线中直接进行 SOP 步骤识别，无需离开推理管线。
    """
    step_classes: dict = None  # {class_id: step_name}
    
    def __call__(self, detections: list) -> list:
        """识别当前 SOP 步骤"""
        results = []
        for det in detections:
            class_id = det.class_id
            if class_id in self.step_classes:
                results.append({
                    "step": self.step_classes[class_id],
                    "confidence": det.confidence,
                    "bbox": det.bbox
                })
        return results
```

### 4.3 多模型编排（模拟 WG Tech 的 45 模型策略）

```python
class MultiModelOrchestrator:
    """
    多模型编排器 — 模拟 WG Tech 的 45 个专用模型按需调用策略。
    DeepInsight 的核心：根据工位和任务选择正确的模型。
    """
    
    def __init__(self):
        # 每个工位/任务对应一个专用模型
        self.models = {
            "station_1_assembly": self._load_pipeline("assembly_step1.axm"),
            "station_1_ppe": self._load_pipeline("ppe_helmet_vest.axm"),
            "station_2_pallet": self._load_pipeline("pallet_handling.axm"),
            "station_2_hand_pose": self._load_pipeline("hand_pose_yolo11.axm"),
            "station_3_quality": self._load_pipeline("defect_inspection.axm"),
            "station_3_tools": self._load_pipeline("tool_identification.axm"),
            "cctv_staffing": self._load_pipeline("person_tracking.axm"),
            # ... 更多专用模型
        }
        
        # 工位到模型的映射
        self.station_model_map = {
            "station_1": ["station_1_assembly", "station_1_ppe"],
            "station_2": ["station_2_pallet", "station_2_hand_pose"],
            "station_3": ["station_3_quality", "station_3_tools"],
            "cctv_room": ["cctv_staffing"],
        }
    
    def _load_pipeline(self, model_path: str):
        """加载编译后的模型管线"""
        return op.seq(
            op.color_convert("RGB", src="BGR"),
            op.letterbox(640, 640),
            op.to_tensor(),
            op.load(model_path),
            ConfidenceFilter(threshold=0.25),
            op.to_image_space(),
        ).optimized()
    
    def process_station(self, station_id: str, frame: np.ndarray) -> dict:
        """
        处理特定工位的视频帧 — 只运行该工位需要的模型
        """
        model_names = self.station_model_map.get(station_id, [])
        results = {}
        
        for model_name in model_names:
            pipeline = self.models[model_name]
            detections = pipeline(frame)
            results[model_name] = detections
        
        return results
```

---

## 五、Ultralytics YOLO 导出到 Axelera（完整代码）

### 5.1 模型导出

```python
from ultralytics import YOLO

# ============================================================
# 步骤 1：训练专用模型（以 PPE 检测为例）
# ============================================================
model = YOLO("yolo11n.pt")  # 加载预训练 YOLO11 Nano

# 在自定义数据集上微调
model.train(
    data="ppe_detection.yaml",    # 数据集配置
    epochs=100,
    imgsz=640,
    batch=32,
    device=0,                      # GPU
)

# ============================================================
# 步骤 2：导出为 Axelera 格式
# ============================================================
model.export(
    format="axelera",              # 目标格式
    imgsz=640,                     # 输入尺寸
    quantize=8,                    # INT8 量化（Axelera AIPU 需要）
    data="ppe_calibration.yaml",   # 量化校准数据集
    fraction=1.0,                  # 使用 100-400 张图像校准
)

# 导出后生成目录：
# ppe_model_axelera_model/
# ├── ppe_model.axm                # Axelera 模型文件
# ├── compiler_config_final.toml   # 编译器配置
# └── metadata.yaml                # 模型元数据（类别、图像尺寸等）
```

### 5.2 CLI 导出方式

```bash
# 导出 YOLO11 检测模型
yolo export model=yolo11n.pt format=axelera

# 导出 YOLO11 姿态估计模型（用于工人姿态分析）
yolo export model=yolo11n-pose.pt format=axelera

# 导出 YOLO11 分割模型（用于缺陷检测）
yolo export model=yolo11n-seg.pt format=axelera

# 导出 YOLOv8 已有模型（保持现有管线）
yolo export model=yolov8n.pt format=axelera
```

### 5.3 导出配置参数

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `format` | str | 'axelera' | 目标格式 |
| `imgsz` | int | 640 | 输入图像尺寸 |
| `batch` | int | 1 | 批处理大小 |
| `quantize` | int/str | 8 (INT8) | 量化精度 |
| `data` | str | 'coco128.yaml' | 量化校准数据集 |
| `fraction` | float | 1.0 | 校准数据比例（推荐 100-400 张） |
| `device` | str | None | 导出设备 (GPU=0 或 CPU=cpu) |

### 5.4 性能基准（来自 Ultralytics 官方文档）

| 模型 | Metis PCIe FPS | Metis M.2 FPS |
|------|---------------|---------------|
| YOLOv8n | 847 | 771 |
| YOLO11n | 746 | 574 |
| YOLO26n | 648.6 | 484.9 |

---

## 六、完整推理示例代码（Ultralytics 官方）

### 6.1 YOLO26 姿态估计 + 多目标跟踪（完整源码）

以下为 Ultralytics 官方 GitHub 仓库中的完整示例代码，**直接适用于 SOP 场景中的工人姿态监控**：

> 来源：https://github.com/ultralytics/ultralytics/blob/main/examples/YOLO-Axelera-Python/yolo26-pose-tracker.py

```python
"""
Ultralytics YOLO26 Pose Estimation with optional Multi-Object Tracking
using Axelera Voyager SDK.

Standalone example using the axelera-rt pipeline API.
No ultralytics dependency at runtime.

YOLO26 is NMS-free: outputs (1, 300, 57) already in final format:
[x0, y0, x1, y1, score, class_id, kpt0_x, kpt0_y, kpt0_conf, ...]
"""

from __future__ import annotations
import argparse
import colorsys
import cv2
import numpy as np
from axelera.runtime import op

# COCO skeleton: 19 limb connections (1-indexed keypoint pairs)
COCO_SKELETON = [
    [16, 14], [14, 12], [17, 15], [15, 13], [12, 13],
    [6, 12], [7, 13], [6, 7], [6, 8], [7, 9],
    [8, 10], [9, 11], [2, 3], [1, 2], [1, 3],
    [2, 4], [3, 5], [4, 6], [5, 7],
]

POSE_PALETTE = np.array([
    [255, 128, 0], [255, 153, 51], [255, 178, 102], [230, 230, 0],
    [255, 153, 255], [153, 204, 255], [255, 102, 255], [255, 51, 255],
    [102, 178, 255], [51, 153, 255], [255, 153, 153], [255, 102, 102],
    [255, 51, 51], [153, 255, 153], [102, 255, 102], [51, 255, 51],
    [0, 255, 0], [0, 0, 255], [255, 0, 0], [255, 255, 255],
], dtype=np.uint8)

KPT_COLORS = POSE_PALETTE[[16, 16, 16, 16, 16, 0, 0, 0, 0, 0, 0, 9, 9, 9, 9, 9, 9]]
LIMB_COLORS = POSE_PALETTE[[9, 9, 9, 9, 7, 7, 7, 0, 0, 0, 0, 0, 16, 16, 16, 16, 16, 16, 16]]
GOLDEN_RATIO_CONJUGATE = 0.618033988749895


def get_track_color(track_id: int) -> tuple[int, int, int]:
    """Generate consistent BGR color for a track_id."""
    hue = (track_id * GOLDEN_RATIO_CONJUGATE) % 1.0
    r, g, b = colorsys.hsv_to_rgb(hue, 0.8, 0.95)
    return (int(b * 255), int(g * 255), int(r * 255))


class ConfidenceFilter(op.Operator):
    """Squeeze batch dimension and filter detections by confidence score."""
    threshold: float = 0.25
    score_col: int = 4

    def __call__(self, x: np.ndarray) -> np.ndarray:
        if x.ndim == 3:
            x = x[0]
        return x[x[:, self.score_col] >= self.threshold]


def build_pipeline(model_path: str, conf: float = 0.25, 
                   tracker_algo: str | None = "tracktrack"):
    """Build the YOLO26 pose pipeline, optionally with tracking."""
    stages = [
        op.color_convert("RGB", src="BGR"),
        op.letterbox(640, 640),
        op.to_tensor(),
        op.load(model_path),
        ConfidenceFilter(threshold=conf),
        op.to_image_space(keypoint_cols=range(6, 57, 3)),
    ]
    if tracker_algo:
        stages.append(op.ax_pose(num_keypoints=17, class_id_type=op.CocoClasses))
        stages.append(op.tracker(algo=tracker_algo))
    return op.seq(*stages).optimized()


def draw_pose(image, detections, conf=0.25):
    """Draw pose results from raw model rows."""
    h, w = image.shape[:2]
    for det in detections:
        score = float(det[4])
        if score < conf:
            continue
        x0, y0 = int(det[0] * w), int(det[1] * h)
        x1, y1 = int(det[2] * w), int(det[3] * h)
        cv2.rectangle(image, (x0, y0), (x1, y1), (0, 255, 0), 2)
        cv2.putText(image, f"{score:.2f}", (x0, y0 - 5),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 0), 1)
        kpts = det[6:].reshape(-1, 3)
        for i, (a, b) in enumerate(COCO_SKELETON):
            kp_a, kp_b = kpts[a - 1], kpts[b - 1]
            if kp_a[2] > 0.5 and kp_b[2] > 0.5:
                color = tuple(int(c) for c in LIMB_COLORS[i][::-1])
                pt_a = (int(kp_a[0] * w), int(kp_a[1] * h))
                pt_b = (int(kp_b[0] * w), int(kp_b[1] * h))
                cv2.line(image, pt_a, pt_b, color, 2)
        for j, kp in enumerate(kpts):
            if kp[2] > 0.5:
                color = tuple(int(c) for c in KPT_COLORS[j][::-1])
                cv2.circle(image, (int(kp[0] * w), int(kp[1] * h)), 4, color, -1)
    return image


def draw_tracked_poses(image, tracked_poses):
    """Draw tracked pose results with track ID colors."""
    h, w = image.shape[:2]
    for tracked in tracked_poses:
        color = get_track_color(tracked.track_id)
        bbox = tracked.predicted_bbox
        x0, y0 = int(bbox.x0 * w), int(bbox.y0 * h)
        x1, y1 = int(bbox.x1 * w), int(bbox.y1 * h)
        cv2.rectangle(image, (x0, y0), (x1, y1), color, 2)
        cv2.putText(image, f"ID {tracked.track_id}", (x0, y0 - 5),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, color, 2)
        pose = tracked.tracked
        if not hasattr(pose, "keypoints") or not pose.keypoints:
            continue
        kpts = pose.keypoints
        for i, (a, b) in enumerate(COCO_SKELETON):
            kp_a, kp_b = kpts[a - 1], kpts[b - 1]
            if kp_a.confidence > 0.5 and kp_b.confidence > 0.5:
                pt_a = (int(kp_a.x * w), int(kp_a.y * h))
                pt_b = (int(kp_b.x * w), int(kp_b.y * h))
                cv2.line(image, pt_a, pt_b, color, 2)
        for j, kp in enumerate(kpts):
            if kp.confidence > 0.5:
                cv2.circle(image, (int(kp.x * w), int(kp.y * h)), 4,
                           tuple(int(c) for c in KPT_COLORS[j][::-1]), -1)
    return image


def main():
    parser = argparse.ArgumentParser(
        description="Ultralytics YOLO26 Pose Estimation + Tracking - Axelera Voyager SDK"
    )
    parser.add_argument("--model", type=str, required=True, help="Path to compiled .axm model")
    parser.add_argument("--source", type=str, default="0", help="Image, video path, or camera index")
    parser.add_argument("--conf", type=float, default=0.25, help="Confidence threshold")
    parser.add_argument("--tracker", type=str, default="tracktrack",
                        choices=["bytetrack", "oc-sort", "sort", "tracktrack", "none"],
                        help="Tracking algorithm")
    parser.add_argument("--no-display", action="store_true", help="Disable GUI window")
    parser.add_argument("--output", type=str, default="output.mp4", help="Output video path")
    args = parser.parse_args()

    tracker_algo = None if args.tracker == "none" else args.tracker
    pipeline = build_pipeline(args.model, args.conf, tracker_algo)
    use_tracking = tracker_algo is not None

    source = int(args.source) if args.source.isdigit() else args.source
    cap = cv2.VideoCapture(source)
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open source: {args.source}")

    writer = None
    frame_count = 0
    frames = cap.get(cv2.CAP_PROP_FRAME_COUNT)
    is_image = frames == 1

    while True:
        ret, frame = cap.read()
        if not ret:
            break

        results = pipeline(frame)
        annotated = (draw_tracked_poses(frame, results) if use_tracking 
                     else draw_pose(frame, results, args.conf))
        frame_count += 1

        if args.no_display:
            if writer is None:
                h, w = annotated.shape[:2]
                fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
                writer = cv2.VideoWriter(args.output, cv2.VideoWriter_fourcc(*"mp4v"),
                                         fps, (w, h))
            writer.write(annotated)
            if frame_count % 100 == 0:
                print(f"  frame {frame_count}: {len(results)} "
                      f"{'tracks' if use_tracking else 'detections'}")
        else:
            title = ("Ultralytics YOLO26 Pose Tracking" if use_tracking 
                     else "Ultralytics YOLO26 Pose")
            cv2.imshow(title, annotated)
            if cv2.waitKey(0 if is_image else 1) & 0xFF in [ord("q"), ord("Q"), 27]:
                break

    cap.release()
    if writer is not None:
        writer.release()
        print(f"Saved {frame_count} frames to {args.output}")
    else:
        cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
```

**运行方式**：

```bash
# 姿态估计（无跟踪）
python yolo26-pose-tracker.py --model yolo26n-pose.axm --source 0 --tracker none

# 姿态估计 + 多目标跟踪
python yolo26-pose-tracker.py --model yolo26n-pose.axm --source video.mp4

# 使用不同跟踪算法
python yolo26-pose-tracker.py --model yolo26n-pose.axm --source 0 --tracker bytetrack
python yolo26-pose-tracker.py --model yolo26n-pose.axm --source 0 --tracker oc-sort
```

### 6.2 跟踪算法对比

| 算法 | 优势 | 论文 |
|------|------|------|
| **TrackTrack** (默认) | 迭代匹配 + 跟踪感知 NMS (SOTA) | CVPR 2025 |
| **ByteTrack** | 通过双阈值处理低置信度检测 | Zhang et al., ECCV 2022 |
| **OC-SORT** | 观测中心重更新 + 虚拟轨迹 | Cao et al., CVPR 2023 |
| **SORT** | 简单快速的 IoU 基线 | Bewley et al., ICIP 2016 |

---

## 七、WG Tech 硬件产品线

### 7.1 DeepInsight 企业级套件

| 型号 | 硬件 | 最佳用途 | 型号代码 |
|------|------|---------|---------|
| Premium Plus | Axelera PCIe AI | 复杂多模型企业环境 | DII7AP |
| Premium | Axelera M.2 AI | 高吞吐量质量检测 | DIA5AM2 |
| Standard | Axelera Metis AI | 标准工业安全与安防 | DIR3AM2 |

### 7.2 WGDeepInsight 平台能力

| 能力 | 说明 |
|------|------|
| 摄像头管理 | 多路 RTSP 摄像头接入与管理 |
| AI 推理编排 | 多模型按需调度 |
| 模型生命周期管理 | 训练→部署→更新→回滚 |
| 规则自动化 | 可配置的违规判定规则 |
| 告警系统 | 邮件、消息、角色仪表板 |
| 存储与回溯 | 检测记录归档与查询 |
| 企业集成 | ERP/MES 对接 |
| 安全性 | Secure RTSP、本地推理（原始视频不上云）、加密存储 |

---

## 八、对你 SOP-AI 项目的代码参考

### 8.1 架构对照

| WG Tech 组件 | 你的 SOP-AI 对应 | 参考方向 |
|-------------|-----------------|---------|
| Voyager SDK 推理 | ONNX Runtime 推理 | 已有，可参考管线化设计 |
| 45 个专用模型 | 当前单一模型 | 可按 SOP 步骤拆分专用模型 |
| 规则引擎 | 步骤验证逻辑 | 参考上方 SOPStepValidator |
| 多通道告警 | 当前报警机制 | 可扩展邮件/仪表板 |
| DeepInsight 平台 | 你的 WinForms 应用 | 可参考多摄像头管理 |

### 8.2 可直接参考的代码模式

```python
# ============================================================
# 适配你项目的 SOP 监控管线设计（Python 伪代码参考）
# ============================================================

class SOPMonitoringPipeline:
    """
    SOP 监控管线 — 基于 WG Tech 架构模式设计。
    可适配你的 C# / ONNX 项目。
    """
    
    def __init__(self, sop_config: dict):
        # 1. 加载专用模型（按 WG Tech 策略，每个步骤专用模型）
        self.hand_model = self._load_model("hand_pose_21points.onnx")
        self.object_model = self._load_model("sop_objects_detection.onnx")
        self.ppe_model = self._load_model("ppe_detection.onnx")
        
        # 2. 初始化 SOP 步骤验证器
        self.step_validator = SOPStepValidator(sop_config["steps"])
        
        # 3. 初始化告警路由
        self.alert_router = AlertRouter()
        
        # 4. 初始化各场景检查器
        self.ppe_checker = PPEComplianceChecker()
        self.time_tracker = StepTimeTracker(sop_config["time_limits"])
    
    def process_frame(self, frame: np.ndarray) -> dict:
        """处理单帧 — 模拟 WG Tech 的多模型并行推理"""
        results = {
            "hand_pose": self.hand_model(frame),
            "objects": self.object_model(frame),
            "ppe": self.ppe_model(frame),
        }
        
        # 规则引擎验证
        violations = []
        
        # SOP 步骤验证
        detected_step = self._identify_step(results["hand_pose"], results["objects"])
        step_result = self.step_validator.validate(detected_step, time.time())
        if step_result["status"] == "violation":
            violations.append(step_result)
        
        # PPE 合规检查
        ppe_violations = self.ppe_checker.check_compliance(results["ppe"])
        violations.extend(ppe_violations)
        
        # 时间监控
        time_violations = self.time_tracker.check(detected_step)
        violations.extend(time_violations)
        
        # 告警分发
        if violations:
            self.alert_router.route_alert(violations, self.recipients)
        
        return {
            "frame_results": results,
            "current_step": self.step_validator.current_step_index,
            "violations": violations,
            "timestamp": time.time(),
        }
```

---

## 九、关键链接汇总

| 资源 | 链接 |
|------|------|
| WG Tech 案例完整版（中文） | https://www.ultralytics.com/zh/customers/wg-tech-solutions-cuts-safety-violations-by-28-with-ultralytics-yolo-and-axeleras-ai-accelerator |
| Axelera 社区技术深度解析 | https://community.axelera.ai/product-updates/102 |
| Voyager SDK GitHub | https://github.com/axelera-ai-hub/voyager-sdk |
| Ultralytics Axelera 导出指南 | https://docs.ultralytics.com/integrations/axelera |
| YOLO-Axelera Python 示例代码 | https://github.com/ultralytics/ultralytics/tree/main/examples/YOLO-Axelera-Python |
| yolo26-pose-tracker.py 完整源码 | https://github.com/ultralytics/ultralytics/blob/main/examples/YOLO-Axelera-Python/yolo26-pose-tracker.py |
| yolo11-seg.py 完整源码 | https://github.com/ultralytics/ultralytics/blob/main/examples/YOLO-Axelera-Python/yolo11-seg.py |
| WG Tech 官网 | https://www.wgtech.ai |
| WGDeepInsight 平台 | https://www.wgtech.ai/wgdeepinsight |
| WG Tech 质量检测方案 | https://www.wgtech.ai/solutions/quality-inspection |
| WG Tech 企业级套件 | https://www.wgtech.ai/deepinsight-for-enterprise |

---

*报告生成时间：2026-07-28 21:46 (北京时间)*
