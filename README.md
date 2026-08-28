# 视觉检测系统 (Vision Inspection System)

基于 [YoloDotNet](https://github.com/Nick-DiDonato/YoloDotNet) 的工业级机器视觉检测系统，采用**模块化、配置驱动**的插件式架构，面向产线作业合规监测（SOP）、缺陷检测等机器视觉场景。

## 项目简介

系统以 YOLO 系列模型（检测 / 分割 / 姿态 / OBB 等）为核心推理引擎，通过统一的模块接口将不同检测能力解耦为可独立启停的插件；配合 ROI 区域配置、相机管理与产品配置，可在不修改代码的前提下快速切换不同产品的检测流程。UI 基于 WPF + MVVM 实现，便于现场调试与可视化。

## 核心架构

```
VisionInspectionSystem/
├── src/
│   ├── Core/                       # 核心类库（接口、模型、基础服务）
│   │   └── VisionInspection.Core/
│   │       ├── Interfaces/         # IDetectionModule 等模块契约
│   │       ├── Models/             # ROI / CameraConfiguration / ModuleResult / User
│   │       ├── Services/           # 相机、ROI、模型、用户管理
│   │       └── ViewModels/         # MVVM 基础（ViewModelBase、ROI 编辑器）
│   ├── Modules/                    # 可热插拔的检测模块
│   │   ├── VisionInspection.Modules.SOP/      # SOP 合规检测（已实现）
│   │   ├── VisionInspection.Modules.Defect/   # 缺陷检测（模块框架）
│   │   ├── VisionInspection.Modules.Cascade/  # 级联检测（模块框架）
│   │   └── VisionInspection.Modules.Detection/# 通用 YOLO 检测服务
│   └── UI/
│       └── VisionInspection.UI/    # WPF 主界面（MVVM）
├── configs/                        # 配置文件
│   ├── system.json                 # 系统配置
│   └── products/                   # 产品配置（ROI、启用模块、模型等）
├── models/                         # YOLO 模型文件（不纳入仓库）
└── logs/                           # 运行日志
```

## 功能特性

- ✅ **模块化插件架构**：检测模块实现统一的 `IDetectionModule` 接口，可在配置中按需启用 / 停用，支持热插拔与独立扩展。
- ✅ **配置驱动**：通过 `configs/system.json` 与 `configs/products/` 定义产品、ROI、模型与启用模块，切换产品无需修改代码。
- ✅ **ROI 区域管理**：`ROIManager` 支持矩形、圆形等多种感兴趣区域的定义与编辑（`ROIEditorViewModel` 提供可视化编辑）。
- ✅ **相机管理**：`CameraManager` / `ICameraService` 支持真实相机接入，并内置 `MockCameraService` 模拟相机，便于无硬件环境开发与调试；相机参数通过 `CameraConfiguration` 配置。
- ✅ **模型管理**：`IModelManager` 统一加载与管理 YOLO 模型（检测 / 分割 / 姿态估计 / OBB 等）。
- ✅ **SOP 合规检测（已实现）**：基于 YAML 配置描述操作步骤工作流（`SOPYamlConfig` / `SOPWorkflow`），结合 YOLO 姿态估计与 MediaPipe 手部检测判断人员动作，由 `SOPStateMachine` 驱动状态流转，`ViolationDetector` 实时判定违规并报警。
- ✅ **通用 YOLO 检测服务**：`YoloDetectionService` 封装 YoloDotNet，提供目标检测 / 姿态估计等推理能力，供各模块复用。
- ✅ **MVVM 架构**：`ViewModelBase` 等基础类型实现 UI 与业务逻辑解耦，便于维护与测试。
- ✅ **用户与权限**：`UserManager` / `User` 提供基础用户管理。

## 快速开始

> 需要 .NET 8 SDK 与对应的 ONNX 运行时（OnnxRuntime）支持。

```bash
# 还原依赖
dotnet restore

# 编译
dotnet build

# 运行（WPF 主界面，需 Windows 环境）
dotnet run --project src/UI/VisionInspection.UI
```

## 配置说明

- `configs/system.json`：系统级配置（相机、全局参数等）。
- `configs/products/`：按产品组织的配置，包含 ROI 区域、启用的检测模块、所用模型及模块参数；新增 / 切换产品只需增加对应配置文件。
- 模型文件放置于 `models/`（体积较大，默认不纳入版本库，详见 `.gitignore`）。

## 开发计划

| 状态 | 内容 |
| --- | --- |
| ✅ 已完成 | 项目基础结构 |
| ✅ 已完成 | 核心类库（接口、ROI、相机 / 模型 / 用户服务、MVVM 基础） |
| ✅ 已完成 | SOP 合规检测模块（YOLO 姿态 + MediaPipe 手部 + 状态机 + 违规检测） |
| ✅ 已完成 | 通用 YOLO 检测服务、WPF UI 框架 |
| 🔄 进行中 | 缺陷检测模块、级联检测模块（框架已搭建，待业务实现） |
| ⏳ 待规划 | PLC 通信、更多模型 / 算法接入 |

## 许可证

本项目基于 [MIT 许可证](./LICENSE) 开源。
