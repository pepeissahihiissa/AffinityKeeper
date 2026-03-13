# Affinity Keeper (β1)

プロセスのCPUアフィニティ（使用コア）を自動的に監視・固定するツールです。
特定のプロセスが特定のコアを専有するように設定したり、Eコアを避けてPコアのみに割り当てたりすることが可能です。

## 主な機能
- **自動常駐監視**: 起動したプロセスを検知し、設定されたアフィニティを即座に適用します。
- **プロファイル管理**: 全プロセスの設定を「プロファイル」として一括保存・切り替えが可能です。
- **直感的なUI**: CPUコアごとのチェックボックスで簡単に割り当てを変更できます。

## インストール・実行方法
1. [Releases](https://github.com/pepeissahihiissa/AffinityKeeper/releases) から最新の `AffinityKeeper_beta1.zip` をダウンロードします。
2. 任意のフォルダに展開してください。
3. `AffinityKeeper.exe` を実行するとタスクトレイに常駐します。

## 開発者向け（ビルド方法）
- Visual Studio 2022 / .NET 8.0
- `AffinityKeeper.sln` を開き、ビルドしてください。

## ライセンス
MIT License - 詳細は LICENSE ファイルを参照してください。
