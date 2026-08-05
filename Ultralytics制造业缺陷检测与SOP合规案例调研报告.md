# Ultralytics 制造业缺陷检测 & SOP合规案例调研报告

> 调研日期：2026-07-28 | 来源：Ultralytics 官网、Ultralytics 公众号/中文站、Axelera 社区、CSDN

---

## 一、Ultralytics 官网：制造业缺陷检测案例

### 1.1 制造业解决方案总览

**来源页面**：https://www.ultralytics.com/zh/solutions/ai-in-manufacturing

Ultralytics 官网将制造业计算机视觉分为 **5 大场景**：

| 场景 | 说明 |
|------|------|
| 质量检测 (Quality Inspection) | 实时缺陷检测与视觉检查，支持检测、分割、分类、OBB |
| PPE 与安全 (PPE & Safety) | 工人防护装备合规监控、危险行为检测 |
| 预测性维护 (Predictive Maintenance) | 设备状态监控与故障预警 |
| 装配验证 (Assembly Verification) | 组件完整性检查、装配顺序验证 |
| 库存与物流 (Inventory & Logistics) | 物料追踪、库存管理 |

#### 核心精度数据（官网引用学术论文）

| 应用领域 | 关键指标 | 论文来源 |
|---------|---------|---------|
| **电路板缺陷检测** | 99.1% mAP / 99.2% 召回率 | Tang et al., *Micromachines*, 2025 |
| **微米级薄膜筛查** | 714 FPS / >95% 检测率 (NVIDIA RTX 3090) | Yu et al., *Frontiers in AI*, 2025 |
| **纺织品缺陷检测** | 89.4% mAP | Zhou et al., *PLOS One*, 2025 |

#### 技术能力矩阵

| YOLO 任务类型 | 缺陷检测中的应用 |
|--------------|-----------------|
| **目标检测** | 验证复杂装配中每个组件是否存在（如电子外壳螺丝） |
| **实例分割** | 分离产品特定部分，检查尺寸/公差/对齐问题 |
| **语义分割** | 检查运动部件或开关的机械方向（"开"/"关"位置） |
| **图像分类** | 识别偏斜几度的错位标签或贴纸 |
| **姿态估计** | 将成品分类为"合格"/"不合格"/"返工" |
| **旋转目标检测 (OBB)** | 角度感知边界框检测旋转物体 |

---

### 1.2 缺陷检测专项页面

**来源页面**：https://www.ultralytics.com/zh/solutions/defect-detection

#### 可检测的缺陷类型

| 类别 | 具体缺陷 |
|------|---------|
| 表面缺陷 | 涡轮叶片发丝裂纹、印刷标签污迹、划痕、凹痕、涂层不均 |
| 装配缺陷 | 螺丝缺失/未到位、组件缺失、连接器遗漏 |
| 尺寸/公差缺陷 | 尺寸偏差、对齐问题 |
| 方向/位置缺陷 | 运动部件方向错误、开关位置不正确 |
| 标签缺陷 | 标签错位、贴纸偏斜 |
| 内部结构缺陷 | 配合 X 射线/UV 传感器检测肉眼不可见缺陷 |

#### 客户案例精度汇总

| 客户 | 应用场景 | 成果 |
|------|---------|------|
| **Specialvideo** | 食品检测 | 99% 准确率 |
| **Pixelabs** | 通用缺陷检测 | 95% 召回率 |
| **MarineSitu** | 水下监测 | 96%+ 正常运行时间 |
| **Glacier Robotics** | PET 泄漏检测 | 泄漏量降低 70% |
| **eSmart Systems** | 电力巡检 | 巡检时间缩短一半 |
| **Theia Scientific** | 显微镜分析 | 分析速度提高 43 倍 |
| **RapiD Engineering** | 海鲜质量控制 | 实时缺陷检测，部署快 1 周 |
| **Vivity AI** | 通用视觉 AI | 每年节省超 500 万美元 |

---

### 1.3 博客：使用 YOLO11 构建智能制造解决方案

**来源页面**：https://www.ultralytics.com/zh/blog/making-smart-manufacturing-solutions-with-ultralytics-yolo11

#### 四大制造业应用场景

**① 质量控制 — 实例分割**

| 应用 | 检测内容 |
|------|---------|
| 汽车制造 | 油漆瑕疵、面板凹痕、错位、汽车零件分割 |
| 食品生产 | 面包检测与计数、缺失/损坏标记 |
| 电子制造 | 电路板焊接错误、组件缺失、错位 |

**② 工业自动化 — 目标检测与追踪**

- 机械臂拾取和放置操作中的实时物体检测
- 传送带移动物品的精确定位与追踪
- 确保每个零件被正确拾取和放置

**③ 工人安全 — PPE 检测 + 姿态估计**

- 检测工人是否佩戴头盔、高可视性背心等安全装备
- 姿态估计分析工人身体姿势，识别不安全举重动作
- 检测人体关键点（关节、肢体），实时追踪运动
- 标记危险姿势，在伤害发生前干预

**④ 现场效率 — 车辆检测与追踪**

- 混凝土搅拌站等场景的车辆自动检测、分类与追踪
- 监控装载时间、识别瓶颈、改善调度

---

### 1.4 博客：用计算机视觉改善制造业

**来源页面**：https://www.ultralytics.com/zh/blog/improving-manufacturing-with-computer-vision

#### 实例分割在表面检查中的应用

| 行业 | 检测内容 |
|------|---------|
| **金属零件制造** | 汽车/航空航天金属零件表面划痕、凹痕、涂层不均 |
| **纺织品制造** | 织物图案不一致、撕裂、污渍、颜色差异 |
| **电子设备制造** | 电路板焊接错误、组件缺失、错位 |
| **制药** | 药丸缺角（95%准确率）、受污染药丸（99%准确率） |

#### 系统架构模式

```
[工业相机] → 图像采集
    ↓
[预处理] → 去噪、裁剪、亮度校正
    ↓
[YOLO 模型] → 缺陷检测（划痕/凹坑/污渍等）
    ↓
[告警系统] → 标记坐标 + 上传 MES + 声光报警
    ↓
[数据中心] → 检测记录入库 + 质量趋势图 + 模型再训练闭环
```

---

### 1.5 客户案例：RapiD Engineering — 海鲜质量控制

**来源页面**：https://www.ultralytics.com/zh/customers/rapid-engineering-deploys-seafood-quality-control-1-week-faster-with-ultralytics-yolo

| 维度 | 详情 |
|------|------|
| **客户** | RapiD Engineering |
| **应用** | 三文鱼鱼片质量控制 |
| **技术方案** | 双模型流水线：YOLO11 Nano（分割）+ 大型 YOLO11（缺陷检测） |
| **数据集** | 手动标注 20,000+ 张三文鱼图像 |
| **部署硬件** | NVIDIA Jetson |
| **检测内容** | 血斑、黑色素斑点等细微畸形 |
| **云端分析** | RapiD Vision Explorer 与客户 ERP 集成，记录供应商/农场/位置/订单数据 |
| **成果** | 导出工作流每年节省约 1 周工程时间 |

---

### 1.6 客户案例：Kiwitron — 工业安全检测

**来源页面**：https://www.ultralytics.com/zh/customers/kiwitron

| 维度 | 详情 |
|------|------|
| **客户** | Kiwitron（意大利） |
| **产品** | KiwiEye — AI 工业安全系统 |
| **应用** | 工业地板危险检测（叉车与工人接近） |
| **技术** | Ultralytics YOLO + Coral 加速器 |
| **检测距离** | 30 米内实时检测行人、车辆、标志 |
| **附加功能** | 热力图、近碰撞报告、高风险区域识别 |
| **成果** | 显著减少近碰撞事故，客户反馈预防了严重事故 |

---

## 二、Ultralytics 公众号 / 中文站制造业案例

Ultralytics 中文官网（https://www.ultralytics.com/zh）同步发布中文版博客和客户案例，内容与英文版一致。以下为中文站及关联媒体上的补充案例。

### 2.1 YOLOv8 工业缺陷检测实战（CSDN/DevPress）

**来源页面**：https://devpress.csdn.net/v1/article/detail/156467524

#### 项目背景

| 维度 | 详情 |
|------|------|
| **场景** | 电子制造产线电路板表面缺陷检测 |
| **痛点** | 人工抽检缺陷识别率仅 78%，微小划痕/焊点虚接难发现 |
| **模型** | YOLOv8n (Nano) |
| **部署硬件** | Jetson AGX Xavier + T4 GPU 边缘节点 |
| **推理速度** | 32 FPS 稳定帧率，<30ms 推理延迟 |
| **精度** | mAP@0.5 > 0.89 |
| **数据量** | <2000 张标注图像（Mosaic + MixUp 增强） |

#### 技术亮点

- **无锚框 (Anchor-free) 架构**：不需预设先验框，适应不规则形态缺陷
- **CSPDarknet + 改进 PANet**：多尺度特征融合，低分辨率图像也能捕捉微小缺陷
- **Task-Aligned Assigner + DFL**：提升小目标召回率和定位精度
- **误检率 <6%**（含低光照、反光干扰图像）

#### 系统架构

```
[工业相机] → 定时拍摄
    ↓
[边缘计算盒子] → YOLOv8 Docker 容器
    ├─ 预处理：去噪、裁剪、亮度校正
    ├─ 检测：划痕/凹坑/污渍
    └─ 告警：标记坐标 → 上传 MES → 声光报警
    ↓
[中心服务器] → 日志存储 + 报表展示 + 质量趋势图
``#### 部署成果

| 指标 | 改善前 | 改善后 |
|------|--------|--------|
| 缺陷检出率 | 78% | **96%** |
| 误报率 | — | **下降 40%** |
| 年节省人力成本 | — | **约 25 万元/产线** |
| 部署周期 | — | **<2 周** |

#### Docker 容器化部署

```python
# 镜像集成 PyTorch 1.13+ / CUDA 11.8 / cuDNN / Ultralytics
# 体积 <8GB，支持 GPU 直通
from ultralytics import YOLO
model = YOLO("yolov8n.pt")
results = model.train(data="defect_data.yaml", epochs=100, imgsz=640)
```

---

### 2.2 博客：利用 YOLO11 实现更智能的土木工程（含制造缺陷检测）

**来源页面**：https://www.ultralytics.com/zh/blog/smarter-civil-engineering-with-ultralytics-yolo11

- 预制建筑中钢梁和面板发货前的缺陷分析
- 集成到自动扫描系统，跟踪缺陷率、改进质量保证流程
- PPE 自动检测（头盔、手套、背心）
- 施工区域人员跟踪与机械监控

---

## 三、WG Tech Solutions — SOP 合规性检测案例（重点）

### 3.1 案例概述

**来源页面**：https://www.ultralytics.com/zh/customers/wg-tech-solutions-cuts-safety-violations-by-28-with-ultralytics-yolo-and-axeleras-ai-accelerator

**Axelera 社区技术深度解析**：https://community.axelera.ai/product-updates/102

| 维度 | 详情 |
|------|------|
| **公司** | WG Tech Solutions Pvt Ltd（印度班加罗尔） |
| **核心平台** | WGDeepInsight — AI 驱动的视频分析平台 |
| **客户** | 某领先 ODM（原始设计制造商），运营多个工厂设施 |
| **合作方** | Axelera AI（边缘 AI 加速器硬件） |
| **使用的 YOLO 模型** | Ultralytics YOLO11 + Ultralytics YOLOv8 |
| **模型数量** | **约 45 个专用模型**（非单一通用模型） |
| **核心成果** | **工人安全违规行为减少 28%** |

### 3.2 业务挑战

| 挑战 | 详情 |
|------|------|
| 手动监控不可靠 | 大多数组装流程仍为手工操作，安全和合规检查依赖人工目视 |
| 缺乏可见性 | 无法获取准确、客观的时间与运动数据，瓶颈和劳动力利用不足难以发现 |
| 安全违规易遗漏 | PPE 不合规、未经授权访问、物料处理不当等问题在快节奏环境中容易被错过 |
| 响应滞后 | 延迟反应使预防重复违规变得困难 |
| 扩展困难 | 缺乏自动化时，跨多工厂扩展监控规模是主要顾虑 |

### 3.3 技术架构

#### 整体架构

```
摄像头（多工作站）
    ↓
Voyager SDK（边缘推理）
    ├─ 解码视频流
    ├─ 运行 YOLO 模型（45个专用模型按需调用）
    └─ 输出标注结果与事件
    ↓
规则引擎（Rule-based Alert Logic）
    ├─ 判定何为违规
    └─ 决定告警去向
    ↓
输出层
    ├─ 仪表板（基于角色的 Dashboard）
    ├─ 邮件通知
    ├─ 消息系统
    └─ 存储归档
```

#### 硬件部署策略

WG Tech **没有标准化单一硬件**，而是跨 Axelera Metis 全产品线部署：

| 硬件形态 | 部署位置 | 用途 |
|---------|---------|------|
| Metis M.2 卡 | 紧凑边缘盒子（工作站旁） | 轻量推理任务 |
| Metis PCIe 卡 | PC 级工作站 | 需要更高性能的场景 |
| Metis Compute Board | 独立计算节点 | 多路视频流处理 |

> 同一 Voyager 管线在三种硬件上运行，**无需重写推理层**。

#### 模型策略：45 个专用模型 vs 1 个通用模型

| 类别 | 专用模型职责 |
|------|-------------|
| PPE 检测 | 头盔、背心、手套等合规检测 |
| 安全区域监控 | 闯入禁区/危险区域检测 |
| 过程验证 | SOP 步骤顺序与动作验证 |
| 缺件检测 | 装配中缺失零件检测 |
| 缺陷检测 | 产品表面/结构缺陷 |
| 人员监控 | 人员在场、密度、追踪 |
| 安防分析 | 未经授权访问检测 |

> **设计理念**：每个模型的训练针对特定工位的相机角度、光照条件、物体大小、背景杂乱度和操作工作流进行调优，而非用通用模型强行适配所有场景。这带来了显著更高的精度和更少的误报。

#### YOLO11 与 YOLOv8 的分工

| 模型 | 使用场景 | 原因 |
|------|---------|------|
| **YOLO11** | 新增用例 | 追求更高精度和性能 |
| **YOLOv8** | 已有管线 | 已训练、测试并在生产中验证，"能用就不动" |

#### 数据准备

- 三周内从多个工作站收集视频数据
- 使用专有标注界面进行标注
- 数据集用于训练和微调针对工厂环境定制的 YOLO 模型
- 模型通过额外推理逻辑、参数调整和优化技术增强

### 3.4 SOP 合规性检测场景（核心）

#### 场景一：SOP 执行跟踪

| 检测内容 | 说明 |
|---------|------|
| 步骤顺序验证 | 验证每一步是否遵循所需顺序 |
| 步骤完成确认 | 自动判断当前执行步骤及完成状态 |
| 偏差标记 | 过程中任何偏差实时标记 |
| 漏步检测 | 识别漏掉或错误的过程步骤 |

#### 场景二：托盘处理工作流

- 验证物品的拾取和放置是否正确
- 验证每一步是否遵循所需顺序
- 实时标记任何偏差
- **检测单手托盘处理**等不规范操作

#### 场景三：PPE 合规监控

- 检测工人是否佩戴头盔、高可视性背心等
- PPE 使用不当实时标记

#### 场景四：未经授权访问

- 限制区域闯入检测
- 实时告警

#### 场景五：过度拥挤检测

- 工位人员密度监控
- 超阈值告警

#### 场景六：CCTV 监控室人员追踪

- 实时跟踪人员在场情况
- 人员配置低于所需阈值时触发警报

#### 场景七：质量检查工作流

| 检测维度 | 说明 |
|---------|------|
| 工艺顺序验证 | 验证操作步骤顺序正确性 |
| 指定工具使用 | 强化规定工具的使用，检测是否用了错误工具 |
| 任务时间监控 | 监控每个任务所花费的时间 |
| 偏差标记 | 任何偏差实时标记以保持一致标准 |

#### 场景八：物料堆放

- 检测不规则堆叠的箱子
- 物料处理和堆放是否正确

### 3.5 告警与反馈机制

| 通道 | 说明 |
|------|------|
| 电子邮件 | 自动发送违规告警邮件 |
| 消息系统 | 集成到工厂现有消息平台 |
| 角色仪表板 | 基于角色的 Dashboard，不同角色看到不同视图 |
| 实时边缘处理 | 在设备端低延迟处理，缩短响应时间 |

> 告警机制根据客户需求量身定制，灵活集成到现有工厂工作流中。

### 3.6 量化成果

| 指标 | 成果 |
|------|------|
| 工人安全违规行为 | **减少 28%** |
| 响应时间 | 显著缩短（边缘低延迟实时告警） |
| 重复问题 | 明显减少 |
| 安全协议执行 | 更加一致 |
| 关键程序合规 | 持续遵循（正确工具使用、最低人员配置等） |
| 交付速度 | 更快交付定制解决方案 |
| 运营可见性 | 提供数据驱动的持续可见性 |
| 纠正措施 | 通过有针对性的培训支持 |

### 3.7 技术导出格式

YOLO 模型支持多种导出格式以适配混合架构：

```
ONNX | PyTorch | NCNN | TensorRT | CoreML | OpenVINO | LiteRT
TorchScript | PaddlePaddle | MNN | IMX500 | RKNN | Axelera | DeepX | QNN | Hailo
```

### 3.8 未来扩展计划

- 扩展到新的工厂环境和工作流
- 支持从安全和保安监控到工厂车间过程级检查
- 结合边缘部署、实时分析和 Axelera Metis 边缘 AI 加速器
- 提供可扩展的监控和一致的运营见解

---

## 四、对你的 SOP-AI 项目的启示

基于以上案例调研，对你的 SOP-AI 作业监控系统有以下参考价值：

| 方面 | 案例参考 | 对你项目的启示 |
|------|---------|---------------|
| **模型策略** | WG Tech 用 45 个专用模型而非 1 个通用模型 | 可考虑为不同 SOP 步骤/工位训练专用检测模型 |
| **SOP 步骤验证** | WG Tech 托盘处理工作流的拾放验证与顺序检查 | 与你的 8 步手机包装 SOP 检测逻辑一致 |
| **手部跟踪** | YOLO11 姿态估计检测工人关键点 | 你已有 21 点手部跟踪，可扩展到工人姿态安全分析 |
| **PPE 检测** | YOLO11 目标检测监控头盔/背心 | 可作为系统扩展功能 |
| **告警机制** | WG Tech 的多通道告警（邮件/消息/仪表板） | 可参考设计基于角色的告警分发 |
| **边缘部署** | WG Tech 混合硬件 + Voyager SDK | 你的 ONNX 模型 + 本地推理方向正确 |
| **质量检查工作流** | WG Tech 验证工艺顺序、工具使用、时间监控 | 可扩展检测"是否使用正确工具""每步耗时是否合理" |
| **数据闭环** | RapiD Engineering 的云端分析 + 模型再训练 | 可建立检测记录→质量趋势→模型迭代的闭环 |
| **Docker 部署** | CSDN 案例的容器化部署方案 | 简化部署到不同产线的迁移成本 |
| **精度基准** | 电路板 99.1% mAP / 96% 检出率 | 作为你系统精度优化的参考目标 |

---

## 五、关键链接汇总

| 资源 | 链接 |
|------|------|
| Ultralytics 制造业解决方案（中文） | https://www.ultralytics.com/zh/solutions/ai-in-manufacturing |
| Ultralytics 缺陷检测解决方案（中文） | https://www.ultralytics.com/zh/solutions/defect-detection |
| Ultralytics 视觉检查用例 | https://ultralytics.com/use-cases/visual-inspection |
| YOLO11 智能制造博客（中文） | https://www.ultralytics.com/zh/blog/making-smart-manufacturing-solutions-with-ultralytics-yolo11 |
| 用计算机视觉改善制造业（中文） | https://www.ultralytics.com/zh/blog/improving-manufacturing-with-computer-vision |
| WG Tech Solutions 案例完整版（中文） | https://www.ultralytics.com/zh/customers/wg-tech-solutions-cuts-safety-violations-by-28-with-ultralytics-yolo-and-axeleras-ai-accelerator |
| Axelera 社区技术深度解析 | https://community.axelera.ai/product-updates/102 |
| WG Tech 官网-质量检测方案 | https://www.wgtech.ai/solutions/quality-inspection |
| RapiD Engineering 海鲜质控案例 | https://www.ultralytics.com/zh/customers/rapid-engineering-deploys-seafood-quality-control-1-week-faster-with-ultralytics-yolo |
| Kiwitron 工业安全案例 | https://ultralytics.com/customers/kiwitron |
| YOLOv8 工业缺陷检测实战 (CSDN) | https://devpress.csdn.net/v1/article/detail/156467524 |
| Ultralytics YOLO 文档 | https://docs.ultralytics.com/ |
| Ultralytics GitHub | https://github.com/ultralytics/ultralytics |

---

*报告生成时间：2026-07-28 21:21 (北京时间)*
