# 用語集（UI の表記）

画面・README・ツールチップで使う言葉はこの表に合わせます。
コードでは `src/Terms.cs` の `Terms` から取り出して使い、同じものを指す文字列を直接書かないでください。
用語を追加・変更するときは、この表・`Terms.cs`・README を一緒に直します（`--test` の「用語の統一」で一部を確認しています）。

## 方針
- **ScrollVolume のもの**には「ScrollVolume」を付けて、Windows の機能と区別します。
- **Windows のもの**は Windows の画面での呼び名に合わせます（日本語版・英語版とも）。
- 短く。説明はツールチップに回します。
- 同じ操作には同じ言葉を使います（例: クリック動作とホットキーの名前は同じ）。

## ScrollVolume のもの
| 日本語 | English | 意味 | 使わない言い方 |
|---|---|---|---|
| ScrollVolume ミキサー | ScrollVolume Mixer | 独自のミキサー画面（全デバイス × アプリ、メーター付き） | アプリごとの音量、Volume by app、（単独の）ミキサー |
| ScrollVolume ミキサーを開く | Open ScrollVolume Mixer | クリック動作・ホットキー（開いていれば閉じる） | 〜を表示 |
| ScrollVolume の設定 | ScrollVolume settings | 設定画面 | |
| 音量バー | Volume bar | 音量を変えたときに出る表示（OSD） | OSD、ポップアップ |
| ポップアップ／ウィンドウ表示 | pop-up / window | ミキサーの 2 つの表示方法 | |

## Windows のもの
| 日本語 | English | 開くもの |
|---|---|---|
| Windows のサウンド設定 | Windows sound settings | トレイメニューで下の 3 つをまとめる見出し |
| 設定 > システム > サウンド | Settings > System > Sound | `ms-settings:sound` |
| 音量ミキサー | Volume mixer | `sndvol.exe` |
| Windows の音量ミキサーを開く | Open Windows volume mixer | クリック動作の選択肢 |
| サウンドの詳細設定 | More sound settings | `mmsys.cpl`（サウンド コントロール パネル。ツールチップ: 再生デバイスの有効・無効、プロパティ） |

## 音量・ミュート・デバイス
| 日本語 | English | 備考 |
|---|---|---|
| 音量: 42% | Volume: 42% | トレイのツールチップとメニューで共通。ミュート中は「（ミュート）」「 (muted)」を付ける |
| ミュート | Mute | メニューのチェック項目 |
| ミュートの切り替え | Toggle mute | クリック動作・ホットキー |
| 再生デバイス | Playback device | |
| 再生デバイスの切り替え | Next playback device | ホットキー |
| 再生デバイスがありません | No playback device | 単数形で統一 |
| 既定のデバイス | Default device | |
| 既定にする | Set as default | ミキサーのリンク |
| システム音 | System sounds | |

## 場所
| 日本語 | English |
|---|---|
| 場所 | Areas |
| 画面の四隅／四隅 | Screen corners／corners |
| 画面の端／端 | Screen edges／edges |
| タスクバー | Taskbar |
| ScrollVolume のアイコン | ScrollVolume icon |
| 反応範囲 | Size |
| ホイールクリック | Middle-click |
