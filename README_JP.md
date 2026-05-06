# MA2BT

## [简体中文](README.md) | [English](README_EN.md) | [日本語](README_JP.md)

> **VRChat Avatar** 向けの **Modular Avatar** から **BlendTree** への変換最適化ツール  
> 不具合報告やフィードバックは公式 QQ 群 **1047423396** までお寄せください。

MA2BT は Modular Avatar のビルドプロセスの最後に実行されます。条件を満たす Animator レイヤーを一つの Direct BlendTree に統合することで、FX レイヤー数を削減し、アバターのパフォーマンスを向上させます。

Booth：https://puddingkc.booth.pm/items/8309096

## 動作原理

Modular Avatar は、レスポンシブなプロパティ（Object Toggle、Material Setter、Shape Changer など）ごとに独立した Animator レイヤーを生成しますが、これらは実行時の負荷になります。  
MA2BT はビルド完了後にこれらのレイヤーを解析し、シンプルな構造のものを共有レイヤー内の BlendTree ノードに変換します。

```
最適化前:
MA Responsive: Hat       (Layer)
MA Responsive: Glasses   (Layer)
MA Responsive: Jacket    (Layer)
MA Responsive: Shoes     (Layer)

最適化後:
MA_To_BlendTree_Layer    (1 Layer, 1 Direct BlendTree)
  ├── hat_param     → 1D BlendTree
  ├── glasses_param → 1D BlendTree
  ├── jacket_param  → 1D BlendTree
  └── shoes_param   → 1D BlendTree
```

安全に変換できないレイヤー（例：複数パラメータの AND 条件、非即時遷移など）は維持されます。

## 動作環境

- Unity 2022.3 以上
- [VRChat Avatars SDK](https://creators.vrchat.com/sdk/) 3.x
- [Modular Avatar](https://modular-avatar.nadena.dev/) 1.x
- [NDMF](https://ndmf.nadena.dev/) (MA に同梱)

## インストール方法

- VCC: `https://null-k.github.io/vpm-listing/index`

## 使用方法

1. アバターのルートオブジェクト **(Avatar root)** を選択します。
2. コンポーネントを追加: **Add Component > MA2BT > MA2BT**
3. 通常通りアバターをビルドします。MA2BT は最適化フェーズで自動的に実行されます。
4. Console で `[MA2BT]` ログを確認し、変換結果を確認してください。

> ※ AAO などの他のレイヤー統合プラグインを使用している場合、生成された `MA_To_BlendTree_Layer` はそれらのプラグインによってさらに統合されます。統合効果を確認したい場合は、一時的に他の最適化プラグインを外してテストしてください。

## 設定項目

| 选项 | 默认值 | 说明 |
|---|---|---|
| **Compact Mode** | 开启 | 仅在实际存在动画的数值上生成 BlendTree 阈值，减少空动画 |
| **Multi-State Layers** | 关闭 | 尝试转换包含多个条件状态的层（如多值 int 参数），默认关闭以保证安全 |
| **Scan All Layers** | 关闭 | 不仅扫描 MA 生成的层，也会扫描所有符合模式的 FX 层 |

## 変換の条件

以下の条件を **すべて** 満たす場合のみ、レイヤーが変換されます：

- レイヤー名が `MA Responsive:` で始まる（Scan All Layers が ON の場合は無制限）。
- ステートマシンが「デフォルト」と「条件」の 2 ステート構成である（Multi-State Layers が ON の場合はそれ以上も可）。
- すべての Entry Transition 条件が **単一のパラメータ** のみを使用している。
- すべての Transition が即時である（Duration = 0、Exit Time なし）。

## 変換がスキップされる主な理由

以下の場合、変換は行われません：

- **複数パラメータの AND 条件** — 例：メニュー変数と親のオブジェクト状態（`__ActiveSelfProxy`）を同時に参照している場合。
- **非即時遷移** — ブレンド時間（Blend Duration）が設定されている場合。
- **複雑な構造** — サブステートマシンや AnyState 遷移が含まれる場合。

## 謝辞

[丨丿・丶乛](https://space.bilibili.com/299071021) - 動画からインスピレーションをいただきました。本プラグインは当初 `浊鸷` 氏のプラグインをベースに改修を行い、その後一部の命名を継承しつつ、全体のロジックを再構築しました。
