# デモ動画のタイムライン（映像と音で共有する）
W, H = 1920, 1080
FPS = 30
DURATION = 38.0
S = 1.5                      # UI の拡大率（150%）
TASKBAR_H = int(48 * S)      # 72
WA_BOTTOM = H - TASKBAR_H    # 作業領域の下端

TITLE_END = 3.6              # タイトル → デスクトップ
OUTRO_START = 31.0           # デスクトップ → 終わりのカード

START_VOLUME = 37

# トレイのアイコンの中心（video.py の配置と合わせる）
TRAY_ICON = (W - 324, H - TASKBAR_H // 2)

# カーソルのキーフレーム (時刻, x, y)。間はイーズで補間
CURSOR = [
    (0.0, 960, 640),
    (5.0, 960, 640),
    (6.2, W - 1, 0),          # 右上の隅
    (10.6, W - 1, 0),
    (11.8, 0, 540),           # 左の端
    (15.0, 0, 540),
    (16.2, 430, H - 36),      # タスクバー
    (24.8, 430, H - 36),
    (26.0, TRAY_ICON[0], TRAY_ICON[1]),   # トレイのアイコン
    (27.6, TRAY_ICON[0], TRAY_ICON[1]),
    (28.6, 1560, 690),        # ミキサーの上へ
    (40.0, 1560, 690),
]


def _ticks(t0, dt, values):
    return [(t0 + i * dt, v) for i, v in enumerate(values)]


# ホイール 1 目盛りごとの (時刻, 音量, 向き)
WHEEL = []
def _add(t0, dt, values, d):
    for t, v in _ticks(t0, dt, values):
        WHEEL.append((t, v, d))

_add(6.7, 0.36, [38, 39, 40, 41, 42], +1)          # 隅: ゆっくり 1% ずつ
_add(9.0, 0.085, [45, 50, 55, 60, 65, 70], +1)     # 隅: 速く 5% 区切り
_add(12.6, 0.085, [65, 60, 55, 50], -1)            # 端: 速く下げる
_add(13.5, 0.42, [49, 48], -1)                     # 端: ゆっくり
_add(16.8, 0.085, [45, 40], -1)                    # タスクバー: 速く
_add(17.7, 0.42, [41, 42], +1)                     # タスクバー: ゆっくり

# ホイールクリック（ミュートの切り替え）
MIDDLE_CLICKS = [21.0, 23.1]
# 左クリック（ミキサーを開く）
LEFT_CLICKS = [26.5]
MIXER_OPEN = 26.55

# 区間ごとの領域の光（場所の説明用）: (開始, 終了, 種類)
ZONES = [
    (6.0, 10.7, "corner"),
    (11.6, 15.1, "edge"),
    (16.0, 24.9, "taskbar"),
    (25.9, 27.0, "tray"),
]

# 字幕 (開始, 終了, 本文, 補足)
CAPTIONS_MAIN = [
    (5.1, 10.6, "画面の四隅でホイール"),
    (11.0, 15.1, "画面の端でも"),
    (15.5, 20.2, "タスクバーの上でも"),
    (20.3, 24.6, "ホイールクリックでミュート"),
    (25.0, 30.6, "ScrollVolume ミキサー"),
]
CAPTIONS_SUB = [
    (6.4, 8.7, "ゆっくり回すと 1% ずつ"),
    (8.7, 10.6, "速く回すと 5% 区切り  42 → 45 → 50 …"),
    (11.4, 15.1, "四隅・端の反応する場所は設定で選べます"),
    (15.9, 20.2, "小さなスライダーを狙う必要はありません"),
    (20.6, 24.6, "もう一度押すと元の音量に"),
    (25.4, 30.6, "トレイのアイコンをクリック — すべての再生デバイスとアプリを 1 画面に"),
]


def volume_state(t):
    """時刻 t の (音量, ミュート)"""
    v = START_VOLUME
    for tt, vv, _ in WHEEL:
        if tt <= t:
            v = vv
    muted = sum(1 for c in MIDDLE_CLICKS if c <= t) % 2 == 1
    return v, muted


def ease(x):
    x = max(0.0, min(1.0, x))
    return x * x * x * (x * (x * 6 - 15) + 10)   # smootherstep


def cursor_at(t):
    k = CURSOR
    for i in range(len(k) - 1):
        t0, x0, y0 = k[i]
        t1, x1, y1 = k[i + 1]
        if t0 <= t <= t1:
            e = ease((t - t0) / (t1 - t0)) if t1 > t0 else 1
            return x0 + (x1 - x0) * e, y0 + (y1 - y0) * e
    return k[-1][1], k[-1][2]

# 英語版の字幕（README.md・TERMS.md の表記に合わせる）
CAPTIONS_MAIN_EN = [
    (5.1, 10.6, "Turn the wheel at a screen corner"),
    (11.0, 15.1, "…or at a screen edge"),
    (15.5, 20.2, "…or over the taskbar"),
    (20.3, 24.6, "Middle-click to mute"),
    (25.0, 30.6, "ScrollVolume Mixer"),
]
CAPTIONS_SUB_EN = [
    (6.4, 8.7, "Turn slowly for 1% steps"),
    (8.7, 10.6, "Spin fast for 5% steps  42 → 45 → 50 …"),
    (11.4, 15.1, "Choose which corners and edges respond in settings"),
    (15.9, 20.2, "No need to aim for a tiny slider"),
    (20.6, 24.6, "Middle-click again to unmute"),
    (25.4, 30.6, "Click the tray icon: every playback device and app in one panel"),
]
