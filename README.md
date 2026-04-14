# 视觉检测系统 (Vision Inspection System)

基于 YoloDotNet 的工业级视觉检测系统，支持模块化配置驱动架构。

## 项目结构

```
VisionInspectionSystem/
├── src/
│   ├── Core/
│   │   └── VisionInspection.Core/          # 核心类库（接口、模型、服务）
│   ├── Modules/
│   │   ├── VisionInspection.Modules.SOP/   # SOP 合规检测模块
│   │   ├── VisionInspection.Modules.Defect/# 缺陷检测模块
│   │   └── VisionInspection.Modules.Cascade/# 级联检测模块
│   └── UI/
│       └── VisionInspection.UI/            # WPF 主界面
├── configs/                                 # 配置文件
│   ├── system.json                         # 系统配置
│   └── products/                           # 产品配置
├── models/                                  # 模型文件
└── logs/                                    # 日志文件
```

## 功能特性

- ✅ 模块化架构，支持热插拔
- ✅ ROI 区域管理（矩形、圆形等）
- ✅ 配置驱动，无需修改代码即可切换产品
- ✅ SOP 合规检测
- ✅ 多模型级联检测
- ✅ MVVM 架构

## 快速开始

```bash
# 还原依赖
dotnet restore

# 编译
dotnet build

# 运行
dotnet run --project src/UI/VisionInspection.UI
```

## 开发计划

1. ✅ 项目基础结构
2. ✅ 核心类库（接口、ROI）
3. 🔄 UI 基础框架
4. ⏳ SOP 模块实现
5. ⏳ 缺陷检测模块
6. ⏳ 级联检测模块
7. ⏳ PLC 通信

## 文档

- [系统架构设计](../视觉检测系统架构设计.md)
- [模块化配置驱动架构补充](../视觉检测系统-模块化配置驱动架构补充.md)
- [模块开发指南](../视觉检测系统-模块开发指南.md)
- [SOP 模块 YOLO 实现](../视觉检测系统-SOP模块YOLO实现.md)
- [多模型级联实现](../视觉检测系统-多模型级联实现.md)
