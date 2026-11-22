# ScrollVolume

画面の四隅・端、タスクバー、トレイのアイコンの上でマウスホイールを回すと、音量を変えられる Windows 用の常駐ツールです。
トレイには今の音量を数字で表示します。

*Turn the mouse wheel over a screen corner, edge, the taskbar or the tray icon to change the volume. Includes the ScrollVolume Mixer and global hotkeys. Japanese / English UI.*

## 使い方
- `ScrollVolume.exe` を起動すると、トレイに音量の数字が出ます
- 四隅・端・タスクバー・トレイのアイコンの上でホイールを回す → 音量
  - ゆっくり回すと 1% ずつ、速く回すと 5% 区切りで進みます
  - ホイールクリックでミュート
- トレイのアイコンを左クリック → **ScrollVolume ミキサー**
- 右クリック → メニュー（音量・ミュート・再生デバイス・設定など）

## ScrollVolume ミキサー
すべての再生デバイスと、音を出しているアプリを 1 つの画面に並べます。メーターで、どこから音が出ているかが一目で分かります。
- 行の上でホイール＝音量、ホイールクリック＝ミュート
- 「既定にする」で既定の再生デバイスを切り替え
- 右上のボタンでウィンドウ表示に（大きさの変更・最前面に固定ができます）

## 設定
右クリック →「ScrollVolume の設定」。反応する場所、1 目盛りの変化量、ホットキー、表示、自動起動などを変えられます。
設定は `ScrollVolume.ini` に保存されます。

## ビルド
```powershell
.\build.ps1
```
Windows に入っている .NET Framework 4.x の `csc.exe` を使います（追加のインストールは不要）。

## テスト
```powershell
Start-Process .\ScrollVolume.exe -ArgumentList '--test','shots' -RedirectStandardOutput test.txt -Wait -NoNewWindow; Get-Content test.txt -Encoding utf8
```
`shots` フォルダーに画面のスクリーンショットも出力します。

用語の表記は [TERMS.md](TERMS.md) にまとめています。
