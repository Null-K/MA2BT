# MA2BT

> 用于 **VRChat Avatar** 的 **Modular Avatar** 转 **BlendTree** 优化器  
> 遇到问题可以在官方 QQ 群 **1047423396** 中反馈

MA2BT 会在 Modular Avatar 构建流程结束后运行，将符合条件的 Animator 层合并为一个 Direct BlendTree，从而减少 FX 层数量并提升 Avatar 性能。

## 工作原理

Modular Avatar 会为每一个响应式属性（Object Toggle、Material Setter、Shape Changer 等）生成一个独立的 Animator 层，这些层在运行时会带来额外开销。  
MA2BT 会在构建完成后分析这些生成的层，并将其中简单的结构转换为一个共享层中的 BlendTree 节点。

```
优化前:
MA Responsive: Hat       (Layer)
MA Responsive: Glasses   (Layer)
MA Responsive: Jacket    (Layer)
MA Responsive: Shoes     (Layer)

优化后:
MA_To_BlendTree_Layer    (1 Layer, 1 Direct BlendTree)
  ├── hat_param     → 1D BlendTree
  ├── glasses_param → 1D BlendTree
  ├── jacket_param  → 1D BlendTree
  └── shoes_param   → 1D BlendTree
```

无法安全转换的层（例如多参数 AND 条件、非瞬时过渡等）将保持不变。

## 依赖要求

- Unity 2022.3 及以上
- [VRChat Avatars SDK](https://creators.vrchat.com/sdk/) 3.x
- [Modular Avatar](https://modular-avatar.nadena.dev/) 1.x
- [NDMF](https://ndmf.nadena.dev/) (随 MA 自动安装)

## 安装方法

- VCC: `https://null-k.github.io/vpm-listing/index`

## 使用方法

1. 选中你的 Avatar 根节点 **（Avatar root）**
2. 添加组件: **Add Component > MA2BT > MA2BT**
3. 正常构建 Avatar，MA2BT 会在优化阶段自动运行
4. 在 Console 中查看 `[MA2BT]` 日志了解转换结果

> ※ 如果你安装了 AAO 或其他会合并动画层的插件，生成的 MA_To_BlendTree_Layer 层会被这些插件进一步合并。  
> 可以先移除其他优化插件来测试合并的数量和效果。

## 选项说明

| 选项 | 默认值 | 说明 |
|---|---|---|
| **Compact Mode** | 开启 | 仅在实际存在动画的数值上生成 BlendTree 阈值，减少空动画 |
| **Multi-State Layers** | 关闭 | 尝试转换包含多个条件状态的层（如多值 int 参数），默认关闭以保证安全 |
| **Scan All Layers** | 关闭 | 不仅扫描 MA 生成的层，也会扫描所有符合模式的 FX 层 |

## 可转换条件

当且仅当满足以下**所有**条件时，层才会被转换：

- 层名称以 `MA Responsive:` 开头（或开启 Scan All Layers 时不限制）
- 状态机包含 2 个状态：1 个默认状态 + 1 个条件状态（开启 Multi-State Layers 时可支持更多）
- 所有 Entry Transition 条件仅使用 **单一参数**
- 所有 Transition 均为瞬时（Duration = 0，且无 Exit Time）

## 常见跳过原因

以下情况不会被转换：

- **多参数 AND 条件** — 例如：同时依赖菜单参数和父物体状态（`__ActiveSelfProxy`）
- **非瞬时过渡** — 存在混合时间（blend duration）
- **复杂状态机结构** — 包含子状态机或 AnyState 过渡

## 鸣谢

[丨丿・丶乛](https://space.bilibili.com/299071021) - 视频提供了灵感。本插件最初基于 `浊鸷` 的插件进行修改，随后在保留部分命名的基础上对整体逻辑进行了重构。
