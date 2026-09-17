# GitHub Releases

`v`で始まるバージョンタグのpushで `.github/workflows/release.yml` が起動します。
ブランチのpushだけでは公開しません。

## 公開する

変更をコミットし、リモートへpushした後、公開対象コミットにタグを付けます。

```powershell
git push origin main
git tag -a v0.1.0 -m "Sumi v0.1.0"
git push origin v0.1.0
```

`v0.1.0`は例です。既存のタグ・公開済みリリースを再利用せず、次のバージョンを指定してください。
`v0.2.0-beta.1`のように接尾辞を付けるとGitHubのプレリリースになります。
タグは`vMajor.Minor.Patch`、またはそれにSemVerのプレリリース識別子を付けた形式です。
タグ名を.NETのVersionに反映します。通常開発時の既定値はDirectory.Build.propsに従います。

## 実行内容

1. タグのソースを取得し、.NET 10 SDKでオフラインの回帰テストを実行する。
2. Windows x64／Arm64をRelease・self-contained・トリミングなしでpublishする。
3. 実行ファイル、依存DLL、.NETランタイム、GPLライセンス、同梱ランタイムのライセンス、起動手順をZIPにまとめる。
4. ZIPごとにSHA-256ファイルを作成する。
5. 両方のパッケージが成功した後、GitHubの下書きリリースを作成し、添付完了後に公開する。

添付ファイル：

- `Sumi-v0.1.0-win-x64.zip`
- `Sumi-v0.1.0-win-x64.zip.sha256`
- `Sumi-v0.1.0-win-arm64.zip`
- `Sumi-v0.1.0-win-arm64.zip.sha256`

ReleaseのSource codeから同じタグのソースを取得できます。Ollamaやモデルは配布物に含めません。
フォルダー全体を展開してSumi.exeを起動してください。.NETの別途インストールは不要です。
Arm64版はクロスビルドを検証しており、Arm64実機での動作は別途確認が必要です。
コード署名は現在行っていません。

## 権限と再実行

通常のGITHUB_TOKENを使用します。PATや追加のシークレットは不要です。
ビルドジョブはcontents: read、公開ジョブだけcontents: writeです。
リポジトリまたは組織の設定でGitHub ActionsとRelease作成が許可されている必要があります。
利用するGitHub公式ActionsはコミットSHAで固定しています。

失敗時はActionsログを確認してください。添付中の失敗なら下書きが残り、同じ実行の再実行で再開できます。
公開済みリリースが存在する場合は上書きせず失敗します。コードを修正して出し直す場合は新しいタグを使ってください。

## ローカルで配布物だけを作る

```powershell
./scripts/package-release.ps1 -Tag v0.1.0
# x64のみ
./scripts/package-release.ps1 -Tag v0.1.0 -Runtime win-x64
```

出力先は`artifacts/releases`です。この操作はタグ作成・push・GitHub公開・インストールを行いません。
ランタイムパック取得のため、初回publishにはネットワーク接続が必要です。
