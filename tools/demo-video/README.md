# デモ動画の作り方

README に貼るデモ動画（1920×1080・30fps・約 38 秒）を作るスクリプトです。
画面の録画ではなく、デスクトップ・音量バー・トレイの数字を描いています（見た目は `src/Visuals.cs` に合わせています）。ミキサーは `docs/images/mixer_*_dark.png` を使います。
BGM も `music.py` で合成したオリジナル曲です（外部の音源は使っていません）。

- `timeline.py` – 字幕（日本語・英語）、カーソルの動き、ホイールの操作のタイミング
- `music.py` – BGM を合成して `bgm_raw.wav` に書き出す
- `video.py` – フレームを描いて ffmpeg で mp4 にする。音楽は画面の音量に合わせて大きさが変わり、ミュート中は無音になる

## 必要なもの
[uv](https://docs.astral.sh/uv/) と ffmpeg（PATH に入っていること）。Windows のフォント（Segoe UI・游ゴシック・Segoe Fluent Icons）を使います。

## 作り方
このフォルダーで:
```powershell
uv run --no-project -p 3.12 --with numpy --with scipy python music.py bgm_raw.wav
uv run --no-project -p 3.12 --with numpy --with scipy --with pillow python video.py ..\.. bgm_raw.wav ScrollVolume_demo.mp4
uv run --no-project -p 3.12 --with numpy --with scipy --with pillow python video.py ..\.. bgm_raw.wav ScrollVolume_demo_en.mp4 --lang en
```
`--preview 8.0,21.5` を付けると、その時刻のコマだけを PNG で書き出します（確認用）。

GitHub の README に載せるときは、github.com の編集画面に mp4 をドラッグして貼ります（10 MB まで）。
