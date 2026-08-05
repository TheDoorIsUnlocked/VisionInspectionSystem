---
name: yaml-persistence-debug
description: 调试 .NET 项目中 YAML 配置"源文件正确但运行时字段全空"的问题。覆盖 YamlDotNet NamingConvention 与 Alias 冲突、csproj PreserveNewest 陷阱、bin 下文件污染三大根因。
agent_created: true
---

# YAML 配置运行时字段全空调试指南

## 适用场景

- YAML 源文件字段正确（如 `from_region: "phone_table"`），但运行时日志显示字段为空（`from=''`）
- 修改了源 YAML 后重新编译，运行时行为不变
- 使用了 YamlDotNet 的 `[YamlMember(Alias=...)]` + NamingConvention 组合
- 项目用 csproj `CopyToOutputDirectory=PreserveNewest` 复制配置文件到 bin

## 三大根因及排查步骤

### 根因 1：bin 下 YAML 被污染（最常见）

**现象**：源 YAML 正确，但运行时读的是 bin 下的副本，该副本被程序的"保存"功能写坏。

**排查**：
1. 找到运行时实际加载路径（通常是 `bin/Debug/.../configs/xxx.yaml`）
2. `diff` 源 YAML 与 bin 下 YAML，确认是否一致
3. 如果不一致，bin 下 YAML 被"保存"功能写坏了

**修复**：
```bash
rm bin/Debug/*/configs/xxx.yaml bin/Release/*/configs/xxx.yaml
dotnet build  # 重新编译让 csproj 复制正确的源文件
```

### 根因 2：csproj PreserveNewest 不覆盖

**现象**：修改源 YAML 后 `dotnet build`，bin 下 YAML 不更新。

**原因**：`PreserveNewest` 策略——如果 bin 下文件时间戳比源文件新（被程序保存过），编译不会覆盖。

**修复**：手动删除 bin 下 YAML 文件，再重新编译。

### 根因 3：YamlDotNet NamingConvention 与 Alias 冲突

**现象**：保存功能往返后，snake_case 字段（如 `from_region`、`target_object`）变成空字符串。

**原因**：反序列化器用了 `CamelCaseNamingConvention`，但模型属性只有 `[YamlMember(Alias="from_region")]`。在有 NamingConvention 时，YamlDotNet 可能用 naming convention 转换后的属性名（`fromRegion`）去匹配 YAML key，而非用 alias（`from_region`）。配合 `IgnoreUnmatchedProperties()`，不匹配的字段被静默丢弃。

**关键规则**：序列化器和反序列化器必须用同一套命名约定。如果属性有 `[YamlMember(Alias=...)]`，最佳实践是**不使用任何 NamingConvention**，完全靠 alias 控制字段名。

**错误写法**：
```csharp
// 反序列化用 CamelCase，序列化不用 → 往返不一致
var deserializer = new DeserializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance)  // BUG
    .IgnoreUnmatchedProperties()
    .Build();
var serializer = new SerializerBuilder().Build();  // 无命名约定
```

**正确写法**：
```csharp
// 两者都不用命名约定，全靠 [YamlMember(Alias=...)]
var deserializer = new DeserializerBuilder()
    .IgnoreUnmatchedProperties()
    .Build();
var serializer = new SerializerBuilder().Build();
```

## 快速诊断流程

1. **确认运行时加载路径**：grep 代码中加载 YAML 的相对路径，加上 CWD（通常是 bin 目录）
2. **对比源文件与 bin 文件**：`diff source.yaml bin/.../source.yaml`
3. **如果 bin 文件被污染**：
   - 检查保存功能的序列化/反序列化代码是否 NamingConvention 一致
   - 删除 bin 下污染文件，重新编译
4. **如果 bin 文件正确但运行时仍空**：
   - 检查运行时加载代码（如 `ParseYaml`）是否也用了不一致的 NamingConvention
   - 检查 `[YamlMember(Alias=...)]` 是否覆盖了所有需要映射的字段

## 本项目具体位置

- 源 YAML：`src/UI/VisionInspection.UI/configs/sop/*.yaml`
- bin YAML：`src/UI/VisionInspection.UI/bin/Debug/net8.0-windows/configs/sop/*.yaml`
- 序列化/反序列化：`src/Modules/VisionInspection.Modules.SOP/Models/SOPYamlConfig.cs`
  - `ParseYaml`（运行时加载）：不用 NamingConvention（正确）
  - `SaveRegions`（标定保存）：不用 NamingConvention（已修复）
  - `SaveToYaml`（完整保存）：不用 NamingConvention（正确）
- 保存入口：`src/UI/VisionInspection.UI/Views/SOPRegionEditorWindow.xaml.cs` `Save_Click`
