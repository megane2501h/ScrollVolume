# ScrollVolume のデモ動画を描いて書き出す
#   python video.py <repo> <bgm_raw.wav> <out.mp4> [--preview t1,t2,...]
import sys, os, subprocess, math
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops
from scipy.io import wavfile
from scipy.signal import butter, sosfilt
from timeline import *

REPO = sys.argv[1]
BGM = sys.argv[2]
OUT = sys.argv[3]
PREVIEW = None
if "--preview" in sys.argv:
    PREVIEW = [float(x) for x in sys.argv[sys.argv.index("--preview") + 1].split(",")]
LANG = sys.argv[sys.argv.index("--lang") + 1] if "--lang" in sys.argv else "ja"
EN = LANG == "en"

# 言語ごとの文言（英語は README.md・TERMS.md の表記に合わせる）
T_TITLE_SUB = "Turn the wheel at the screen border to change the volume." if EN else "画面のふちでホイール。音量がすぐ変わる。"
T_TITLE_AREAS = "Corners  ·  Edges  ·  Taskbar  ·  Tray icon" if EN else "四隅 ・ 端 ・ タスクバー ・ トレイのアイコン"
T_OUTRO = [
    "Wheel anywhere at the screen border",
    "1% steps when slow, 5% steps when fast",
    "Middle-click to mute",
    "ScrollVolume Mixer: every device and app",
    "A single ~220 KB .exe, no installer",
] if EN else None
T_FOOTER = "Windows 10 / 11  ·  Free  ·  MIT License" if EN else "Windows 10 / 11  ・  無料  ・  MIT ライセンス"
T_MUTED = "Muted" if EN else "ミュート"

FONTS = "C:/Windows/Fonts/"
def font(name, size, index=0):
    return ImageFont.truetype(FONTS + name, size, index=index)

SEGOE = lambda s: font("segoeui.ttf", s)
SEGOE_B = lambda s: font("segoeuib.ttf", s)
SEGOE_SB = lambda s: font("seguisb.ttf", s)
YU_B = lambda s: font("YuGothB.ttc", s)
YU_M = lambda s: font("YuGothM.ttc", s)
# 字幕・カード用（英語は Segoe UI）
TXT_B = (lambda s: font("seguisb.ttf", s)) if EN else YU_B
TXT_M = (lambda s: font("segoeui.ttf", s)) if EN else YU_M
ICONS = lambda s: font("SegoeIcons.ttf", s)

ACCENT = (76, 160, 255)
ACCENT_DEEP = (0, 120, 212)
MUTE_RED = (232, 72, 72)

# ------------------------------------------------------------ 音
SR, bgm = wavfile.read(BGM)
bgm = bgm.astype(np.float32)
NS = int(DURATION * SR)
bgm = np.pad(bgm, ((0, max(0, NS - len(bgm))), (0, 0)))[:NS]
ts = np.arange(NS) / SR

def amp_for(v, muted):
    return 0.0 if muted else (v / 100.0) ** 1.3

# 音量の変化を音楽に反映（デモの音量 = 聞こえる音量）
gain = np.empty(NS, np.float32)
events = sorted([(t, None) for t in MIDDLE_CLICKS] + [(t, v) for t, v, _ in WHEEL])
v, muted = START_VOLUME, False
target = np.zeros(NS, np.float32)
last = 0
cur = amp_for(v, muted)
for t, ev in events:
    i = int(t * SR)
    target[last:i] = cur
    if ev is None: muted = not muted
    else: v = ev
    cur = amp_for(v, muted)
    last = i
target[last:] = cur
# 終わりのカードでは少し上げる
o0, o1 = int(OUTRO_START * SR), int((OUTRO_START + 1.0) * SR)
target[o0:o1] = np.linspace(target[o0], amp_for(70, False), o1 - o0)
target[o1:] = amp_for(70, False)
# 25 ms でなめらかに
sos = butter(1, 1 / (2 * math.pi * 0.025) / (SR / 2), output="sos")
gain = sosfilt(sos, target - target[0]) + target[0]
# 頭のフェードイン・最後のフェードアウト
gain *= np.clip(ts / 0.8, 0, 1) * np.clip((DURATION - ts) / 2.5, 0, 1)
music = bgm * gain[:, None]

# マウスのクリック音（音量とは無関係に小さく）
rng = np.random.default_rng(3)
def click_sound(level, decay=0.004, hp_fc=2500):
    n = int(0.03 * SR); t = np.arange(n) / SR
    s = rng.standard_normal(n) * np.exp(-t / decay)
    s = sosfilt(butter(2, hp_fc / (SR / 2), btype="high", output="sos"), s)
    return s * level
fx = np.zeros(NS, np.float32)
def put(t, s):
    i = int(t * SR); j = min(NS, i + len(s)); fx[i:j] += s[: j - i]
for t, _, _ in WHEEL:
    put(t, click_sound(0.05, 0.0025, 3000))
for t in MIDDLE_CLICKS + LEFT_CLICKS:
    put(t, click_sound(0.12)); put(t + 0.09, click_sound(0.07))
mix = music + fx[:, None]
mix = mix / max(1e-9, np.max(np.abs(mix))) * 0.89
AUDIO = os.path.splitext(OUT)[0] + ".wav"
wavfile.write(AUDIO, SR, mix.astype(np.float32))

# プレーヤーのビジュアライザー用。プレーヤーが出している音（Windows の音量・ミュートの前）なので
# ScrollVolume の操作では変わらない。頭と最後のフェードだけ反映する
mono = bgm.mean(axis=1) * (np.clip(ts / 0.8, 0, 1) * np.clip((DURATION - ts) / 2.5, 0, 1)) * 0.6
NB = 28
FFT_N = 4096
freqs = np.fft.rfftfreq(FFT_N, 1 / SR)
edges = np.geomspace(50, 12000, NB + 1)
win = np.hanning(FFT_N)
def spectrum(t):
    i = int(t * SR) - FFT_N // 2
    if i < 0 or i + FFT_N > NS: return np.zeros(NB)
    sp = np.abs(np.fft.rfft(mono[i:i + FFT_N] * win))
    out = np.zeros(NB)
    for b in range(NB):
        m = (freqs >= edges[b]) & (freqs < edges[b + 1])
        out[b] = sp[m].mean() if m.any() else 0
    db = 20 * np.log10(out + 1e-6)
    return np.clip((db + 10) / 50, 0, 1)

# ------------------------------------------------------------ 描画の部品
def rr(draw, box, r, **kw):
    draw.rounded_rectangle(box, r, **kw)

def text_w(f, s):
    b = f.getbbox(s); return b[2] - b[0]

def make_wallpaper():
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    base = np.zeros((H, W, 3), np.float32)
    top = np.array([12, 18, 40]); bot = np.array([22, 30, 70])
    k = (yy / H)[..., None]
    base[:] = top * (1 - k) + bot * k
    def blob(cx, cy, r, col, a):
        d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / r
        w = np.exp(-d ** 2)[..., None] * a
        return w * np.array(col)
    base += blob(1350, 820, 620, (40, 110, 255), 0.75)
    base += blob(700, 980, 520, (120, 60, 230), 0.55)
    base += blob(1650, 300, 500, (0, 170, 220), 0.35)
    base += blob(300, 200, 420, (60, 40, 140), 0.35)
    # 波のリボン（Windows 風の柔らかい曲面）
    wave = np.exp(-((yy - (700 - 180 * np.sin(xx / 520 + 0.6))) / 90) ** 2)[..., None]
    base += wave * np.array([60, 130, 255]) * 0.35
    noise = np.random.default_rng(1).normal(0, 1.2, (H, W, 1))
    return Image.fromarray(np.clip(base + noise, 0, 255).astype(np.uint8), "RGB")

WALL = make_wallpaper()

def load_icon(size):
    im = Image.open(os.path.join(REPO, "src", "app.ico"))
    best = max(im.ico.sizes())
    im.size = best
    return im.convert("RGBA").resize((size, size), Image.LANCZOS)

APP_ICON = load_icon(200)

MIXER = Image.open(os.path.join(REPO, "docs", "images", f"mixer_{LANG}_dark.png")).convert("RGBA")

def shadow(img, radius=24, offset=(0, 10), opacity=150):
    """img の形の影を付けた RGBA を返す（余白つき）"""
    pad = radius * 3
    w, h = img.size
    sh = Image.new("RGBA", (w + pad * 2, h + pad * 2), (0, 0, 0, 0))
    a = img.split()[3].point(lambda x: opacity if x > 0 else 0)
    blk = Image.new("RGBA", img.size, (0, 0, 0, 255)); blk.putalpha(a)
    sh.paste(blk, (pad + offset[0], pad + offset[1]), blk)
    sh = sh.filter(ImageFilter.GaussianBlur(radius))
    sh.alpha_composite(img, (pad, pad))
    return sh, pad

def rounded_mask(size, r):
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, size[0] - 1, size[1] - 1), r, fill=255)
    return m

# ミキサー（角を丸めて影を付ける）
mx = MIXER.copy(); mx.putalpha(rounded_mask(mx.size, 12))
MIXER_SH, MIXER_PAD = shadow(mx, 30, (0, 14), 170)

# プレーヤーのウィンドウ（固定部分）
PW_X, PW_Y, PW_W, PW_H = 470, 200, 860, 550
def make_player():
    im = Image.new("RGBA", (PW_W, PW_H), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    rr(d, (0, 0, PW_W - 1, PW_H - 1), 12, fill=(32, 32, 36, 255), outline=(70, 70, 76, 255))
    # タイトルバー
    d.text((24, 15), "\uE8D6", font=ICONS(22), fill=(255, 160, 80))
    d.text((60, 12), "Player", font=SEGOE(22), fill=(230, 230, 235))
    for i, g in enumerate(["\uE921", "\uE922", "\uE8BB"]):
        d.text((PW_W - 160 + i * 52, 16), g, font=ICONS(16), fill=(220, 220, 225))
    # ジャケット
    art = Image.new("RGB", (300, 300))
    yy, xx = np.mgrid[0:300, 0:300]
    a = np.zeros((300, 300, 3))
    a[..., 0] = 255 * (0.95 - 0.4 * yy / 300); a[..., 1] = 110 + 60 * xx / 300; a[..., 2] = 90 + 120 * yy / 300
    sun = np.sqrt((xx - 150) ** 2 + (yy - 170) ** 2) < 70
    a[sun] = [255, 214, 120]
    for k in range(5):
        band = (yy > 200 + k * 18) & (yy < 208 + k * 18)
        a[band] = a[band] * 0.55
    art = Image.fromarray(a.clip(0, 255).astype(np.uint8))
    art_rgba = art.convert("RGBA"); art_rgba.putalpha(rounded_mask((300, 300), 14))
    im.alpha_composite(art_rgba, (40, 80))
    d.text((380, 92), "Sunset Drive", font=SEGOE_SB(44), fill=(245, 245, 250))
    d.text((382, 152), "ScrollVolume Demo BGM", font=SEGOE(24), fill=(170, 170, 180))
    # 操作ボタン
    cx = 380
    for g, size in (("\uE892", 26), ("\uE769", 34), ("\uE893", 26)):
        d.text((cx, 490), g, font=ICONS(size), fill=(235, 235, 240), anchor="lm")
        cx += 70
    return im

PLAYER = make_player()
PLAYER_SH, PLAYER_PAD = shadow(PLAYER, 28, (0, 16), 150)

# デスクトップの固定部分（壁紙・プレーヤー・タスクバー）
def make_desktop():
    im = WALL.convert("RGBA")
    im.alpha_composite(PLAYER_SH, (PW_X - PLAYER_PAD, PW_Y - PLAYER_PAD))
    # タスクバー（すりガラス風）
    tb = im.crop((0, WA_BOTTOM, W, H)).filter(ImageFilter.GaussianBlur(30))
    over = Image.new("RGBA", tb.size, (28, 28, 34, 215))
    tb = Image.alpha_composite(tb, over)
    im.paste(tb, (0, WA_BOTTOM))
    d = ImageDraw.Draw(im)
    d.line((0, WA_BOTTOM, W, WA_BOTTOM), fill=(70, 70, 80), width=1)
    cy = WA_BOTTOM + TASKBAR_H // 2
    # 中央のボタン
    xs = [W // 2 - 150 + i * 60 for i in range(6)]
    # スタート
    x = xs[0]; s = 15
    for dx, dy in ((-s - 1, -s - 1), (1, -s - 1), (-s - 1, 1), (1, 1)):
        d.rectangle((x + dx, cy + dy, x + dx + s, cy + dy + s), fill=(64, 156, 255))
    d.text((xs[1], cy), "\uE721", font=ICONS(28), fill=(235, 235, 240), anchor="mm")
    # フォルダー
    x = xs[2]
    d.rounded_rectangle((x - 18, cy - 13, x + 18, cy + 15), 4, fill=(255, 196, 64))
    d.rounded_rectangle((x - 18, cy - 17, x - 2, cy - 8), 3, fill=(240, 170, 40))
    # ブラウザ
    x = xs[3]
    d.ellipse((x - 17, cy - 17, x + 17, cy + 17), fill=(30, 140, 220))
    d.pieslice((x - 17, cy - 17, x + 17, cy + 17), 200, 330, fill=(60, 200, 140))
    d.ellipse((x - 7, cy - 7, x + 7, cy + 7), fill=(240, 250, 255))
    # Player（起動中）
    x = xs[4]
    hl = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    rr(ImageDraw.Draw(hl), (x - 30, cy - 30, x + 30, cy + 30), 6, fill=(255, 255, 255, 22))
    im.alpha_composite(hl)
    d = ImageDraw.Draw(im)
    rr(d, (x - 17, cy - 17, x + 17, cy + 17), 8, fill=(255, 140, 70))
    d.text((x, cy + 1), "\uE8D6", font=ICONS(20), fill=(255, 255, 255), anchor="mm")
    rr(d, (x - 10, H - 7, x + 10, H - 3), 2, fill=ACCENT)
    # 設定
    d.text((xs[5], cy), "\uE713", font=ICONS(28), fill=(200, 200, 210), anchor="mm")
    # トレイ: シェブロン・(ScrollVolume)・Wi-Fi/スピーカー/電池・時計
    tx = TRAY_ICON[0]
    d.text((tx - 50, cy), "\uE70E", font=ICONS(18), fill=(225, 225, 230), anchor="mm")
    gx = W - 312 + 140
    for i, g in enumerate(["\uE701", "\uE767", "\uE83F"]):
        d.text((W - 300 + 22 + i * 40, cy), g, font=ICONS(22), fill=(235, 235, 240), anchor="mm")
    f = SEGOE(20)
    d.text((W - 24, cy - 13), "20:43", font=f, fill=(240, 240, 245), anchor="rm")
    d.text((W - 24, cy + 13), "2026/11/22", font=f, fill=(240, 240, 245), anchor="rm")
    return im

DESKTOP = make_desktop()

# トレイの数字アイコン（IconStyle.Number を 150% で）
_digit_cache = {}
def tray_number(pct, muted):
    key = (pct, muted)
    if key in _digit_cache: return _digit_cache[key]
    size = int(24 * S)
    big = 400
    f = SEGOE_B(big)
    txt = str(pct)
    zb = f.getbbox("0", anchor="ls"); tb = f.getbbox(txt, anchor="ls")
    ref_h = zb[3] - zb[1]
    maxH = size * 0.80; maxW = size * 1.0
    sy = maxH / ref_h
    w_txt = tb[2] - tb[0]
    sx = min(sy, maxW / w_txt)
    canvas = Image.new("L", (w_txt + 40, ref_h + 40), 0)
    ImageDraw.Draw(canvas).text((20 - tb[0], 20 - zb[1]), txt, font=f, fill=255, anchor="ls")
    canvas = canvas.crop((20, 20, 20 + w_txt, 20 + ref_h))
    nw, nh = max(1, round(w_txt * sx)), max(1, round(maxH))
    canvas = canvas.resize((nw, nh), Image.LANCZOS)
    col = MUTE_RED if muted else (255, 255, 255)
    im = Image.new("RGBA", (size, size), col + (0,))
    solid = Image.new("RGBA", (nw, nh), col + (255,)); solid.putalpha(canvas)
    im.alpha_composite(solid, ((size - nw) // 2, (size - nh) // 2))
    _digit_cache[key] = im
    return im

# 音量バー（Osd の見た目を 150% で）
OSD_W, OSD_H, OSD_M = int(240 * S), int(48 * S), int(20 * S)
def osd_image(pct, muted):
    im = Image.new("RGBA", (OSD_W, OSD_H), (32, 32, 32, 240))
    d = ImageDraw.Draw(im)
    d.rectangle((0, 0, OSD_W - 1, OSD_H - 1), outline=(70, 70, 70, 255))
    pad, textW = 14 * S, 70 * S
    col = MUTE_RED if muted else ACCENT
    if muted:
        d.text((OSD_W - pad, OSD_H / 2), T_MUTED, font=(SEGOE_B if EN else YU_B)(int(13 * S * 1.1)), fill=(255, 255, 255), anchor="rm")
    else:
        d.text((OSD_W - pad, OSD_H / 2), str(pct), font=SEGOE_B(int(20 * S * 1.05)), fill=(255, 255, 255), anchor="rm")
    barX, barW, barH = pad, OSD_W - pad * 3 - textW, 6 * S
    barY = (OSD_H - barH) / 2
    d.rectangle((barX, barY, barX + barW, barY + barH), fill=(80, 80, 80))
    if pct > 0:
        d.rectangle((barX, barY, barX + barW * pct / 100, barY + barH), fill=col)
    return im

def osd_pos(cursor, zone):
    cx_, cy_ = cursor
    minX, maxX = OSD_M, W - OSD_W - OSD_M
    minY, maxY = OSD_M, WA_BOTTOM - OSD_H - OSD_M
    if zone == "corner": return maxX, minY
    if zone == "edge": return minX, int(cy_ - OSD_H / 2)
    return int(max(minX, min(maxX, cx_ - OSD_W / 2))), maxY

# カーソル
def make_cursor():
    k = 2.0
    pts = [(0, 0), (0, 17), (4, 13), (7, 20), (9.4, 19), (6.6, 12.4), (12, 12.4)]
    big = Image.new("RGBA", (int(30 * k * 4), int(30 * k * 4)), (0, 0, 0, 0))
    d = ImageDraw.Draw(big)
    sc = k * 4
    P = [(x * sc + 8, y * sc + 8) for x, y in pts]
    d.polygon(P, fill=(255, 255, 255, 255), outline=(0, 0, 0, 255), width=int(1.4 * sc / 2))
    im = big.resize((big.width // 4, big.height // 4), Image.LANCZOS)
    sh = Image.new("RGBA", im.size, (0, 0, 0, 0))
    a = im.split()[3].point(lambda x: int(x * 0.45))
    blk = Image.new("RGBA", im.size, (0, 0, 0, 255)); blk.putalpha(a)
    sh.paste(blk, (2, 3), blk)
    sh = sh.filter(ImageFilter.GaussianBlur(2))
    sh.alpha_composite(im)
    return sh, 2  # ホットスポットのずれ（8/4）
CURSOR_IMG, CURSOR_HOT = make_cursor()

# ホイールの説明マーク（マウス＋矢印）
def mouse_badge(direction, strength, middle, left):
    s = 120
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.ellipse((4, 4, s - 4, s - 4), fill=(20, 22, 30, 200), outline=(255, 255, 255, 60), width=2)
    bx0, by0, bx1, by1 = 42, 26, 78, 94
    if left > 0:
        d.pieslice((bx0, by0, bx1, by0 + 36), 180, 270, fill=ACCENT + (int(255 * left),))
        d.rectangle((bx0, by0 + 18, 60, 52), fill=ACCENT + (int(255 * left),))
    d.rounded_rectangle((bx0, by0, bx1, by1), 18, outline=(255, 255, 255, 235), width=3)
    d.line((60, by0, 60, 52), fill=(255, 255, 255, 200), width=2)
    d.line((bx0, 52, bx1, 52), fill=(255, 255, 255, 200), width=2)
    wc = ACCENT if (strength > 0 or middle > 0) else (255, 255, 255)
    d.rounded_rectangle((55, 33, 65, 47), 4, fill=wc + (255,))
    if strength > 0 and direction:
        a = int(255 * strength)
        if direction > 0:
            for k, yy in enumerate((22, 12)):
                d.line([(50, yy + 8), (60, yy), (70, yy + 8)], fill=ACCENT + (int(a * (1 - 0.4 * k)),), width=4, joint="curve")
        else:
            for k, yy in enumerate((98, 108)):
                d.line([(50, yy - 8), (60, yy), (70, yy - 8)], fill=ACCENT + (int(a * (1 - 0.4 * k)),), width=4, joint="curve")
    if middle > 0:
        r = 10 + 24 * (1 - middle)
        d.ellipse((60 - r, 40 - r, 60 + r, 40 + r), outline=ACCENT + (int(255 * middle),), width=3)
    return im

# 領域の光
def make_glows():
    g = {}
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    def to_img(alpha, col=ACCENT):
        a = (np.clip(alpha, 0, 1) * 255).astype(np.uint8)
        im = Image.new("RGBA", (W, H), col + (0,)); im.putalpha(Image.fromarray(a, "L")); return im
    d = np.sqrt((xx - W) ** 2 + yy ** 2)
    g["corner"] = to_img(np.exp(-(d / 220) ** 2) * 0.8 + (d < 34) * 0.2)
    g["edge"] = to_img(np.exp(-(xx / 55) ** 2) * 0.65 * (yy < WA_BOTTOM))
    tbm = (yy >= WA_BOTTOM).astype(np.float32)
    edge = np.exp(-((yy - WA_BOTTOM) / 4) ** 2)
    g["taskbar"] = to_img(tbm * 0.16 + edge * 0.8)
    dt = np.sqrt((xx - TRAY_ICON[0]) ** 2 + (yy - TRAY_ICON[1]) ** 2)
    g["tray"] = to_img(np.exp(-(dt / 40) ** 2) * 0.6)
    return g
GLOWS = make_glows()

def with_alpha(im, a):
    if a >= 0.999: return im
    r, g_, b, al = im.split()
    al = al.point(lambda x: int(x * a))
    return Image.merge("RGBA", (r, g_, b, al))

# 字幕（影付き、キャッシュ）
_cap_cache = {}
def caption_img(text, kind):
    key = (text, kind)
    if key in _cap_cache: return _cap_cache[key]
    f = TXT_B(52) if kind == "main" else TXT_M(32 if EN else 30)
    w = text_w(f, text) + 80
    h = 90 if kind == "main" else 60
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    sh = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(sh).text((w / 2, h / 2 + 3), text, font=f, fill=(0, 0, 20, 220), anchor="mm")
    sh = sh.filter(ImageFilter.GaussianBlur(8))
    im.alpha_composite(sh); im.alpha_composite(sh)
    col = (255, 255, 255) if kind == "main" else (200, 222, 255)
    ImageDraw.Draw(im).text((w / 2, h / 2), text, font=f, fill=col, anchor="mm")
    _cap_cache[key] = im
    return im

def seg_alpha(t, t0, t1, fade=0.3):
    if t < t0 or t > t1: return 0.0
    return min(1.0, (t - t0) / fade, (t1 - t) / fade)

# ------------------------------------------------------------ カード（タイトル・終わり）
def card_bg():
    im = WALL.filter(ImageFilter.GaussianBlur(40)).convert("RGBA")
    im.alpha_composite(Image.new("RGBA", (W, H), (8, 10, 22, 170)))
    return im
CARD_BG = card_bg()

def make_title():
    im = CARD_BG.copy()
    icon, pad = shadow(APP_ICON, 24, (0, 12), 160)
    im.alpha_composite(icon, (W // 2 - 100 - pad, 230 - pad))
    d = ImageDraw.Draw(im)
    d.text((W / 2, 545), "ScrollVolume", font=SEGOE_SB(110), fill=(255, 255, 255), anchor="mm")
    d.text((W / 2, 660), T_TITLE_SUB, font=TXT_B(46), fill=(205, 225, 255), anchor="mm")
    d.text((W / 2, 735), T_TITLE_AREAS, font=TXT_M(32), fill=(160, 175, 205), anchor="mm")
    return im
TITLE = make_title()

def make_outro():
    im = CARD_BG.copy()
    icon = APP_ICON.resize((130, 130), Image.LANCZOS)
    icon_s, pad = shadow(icon, 18, (0, 8), 150)
    d = ImageDraw.Draw(im)
    f = SEGOE_SB(92)
    tw = text_w(f, "ScrollVolume")
    total = 130 + 36 + tw
    x0 = (W - total) // 2
    im.alpha_composite(icon_s, (x0 - pad, 170 - pad))
    d.text((x0 + 130 + 36, 235), "ScrollVolume", font=f, fill=(255, 255, 255), anchor="lm")
    items = [
        ("\uE962", "画面のふちならどこでもホイール"),
        ("\uE767", "ゆっくり 1% ずつ、速く回すと 5% 区切り"),
        ("\uE74F", "ホイールクリックでミュート"),
        ("\uE7F4", "ScrollVolume ミキサーで全デバイス・全アプリ"),
        ("\uE7B8", "約 220 KB の .exe 1 つ・インストール不要"),
    ]
    if T_OUTRO:
        items = [(g, s) for (g, _), s in zip(items, T_OUTRO)]
    y = 380
    fx_ = TXT_M(40 if EN else 38)
    widest = max(text_w(fx_, s) for _, s in items) + 70
    lx = (W - widest) // 2
    for g, s in items:
        d.text((lx + 20, y), g, font=ICONS(32), fill=ACCENT, anchor="mm")
        d.text((lx + 70, y), s, font=fx_, fill=(235, 240, 250), anchor="lm")
        y += 72
    # URL のピル
    url = "github.com/megane2501h/ScrollVolume"
    fu = SEGOE_SB(40)
    uw = text_w(fu, url) + 120
    ux = (W - uw) // 2
    rr(d, (ux, 790, ux + uw, 866), 38, fill=(0, 120, 212))
    d.text((ux + 56, 828), "\uE896", font=ICONS(30), fill=(255, 255, 255), anchor="mm")
    d.text((ux + 92, 826), url, font=fu, fill=(255, 255, 255), anchor="lm")
    d.text((W / 2, 930), T_FOOTER, font=TXT_M(30), fill=(160, 175, 205), anchor="mm")
    return im
OUTRO = make_outro()

# ------------------------------------------------------------ フレーム
spec_state = np.zeros(NB)

def zone_for(t):
    for t0, t1, z in ZONES:
        if t0 - 0.2 <= t <= t1 + 0.4: return z, (t0, t1)
    return None, None

def last_tick_before(t):
    best = None
    for tt, v, d in WHEEL:
        if tt <= t: best = (tt, v, d)
    return best

def desktop_frame(t):
    global spec_state
    im = DESKTOP.copy()
    d = ImageDraw.Draw(im)
    vol, muted = volume_state(t)

    # プレーヤーの動く部分
    sp = spectrum(t)
    spec_state = np.maximum(sp, spec_state * 0.82)
    bx, by, bw, bh = PW_X + 380, PW_Y + 200, 440, 150
    n = NB; gap = 4; w1 = (bw - gap * (n - 1)) / n
    for i in range(n):
        hgt = max(3, spec_state[i] * bh)
        x0 = bx + i * (w1 + gap)
        c = (255, 150, 90)
        d.rounded_rectangle((x0, by + bh - hgt, x0 + w1, by + bh), 2, fill=c)
    pos = 72 + t
    px0, px1, py = PW_X + 40, PW_X + PW_W - 40, PW_Y + 410
    d.text((px0, py + 24), f"{int(pos // 60)}:{int(pos % 60):02d}", font=SEGOE(20), fill=(170, 170, 180))
    d.text((px1, py + 24), "3:48", font=SEGOE(20), fill=(170, 170, 180), anchor="ra")
    d.rounded_rectangle((px0, py, px1, py + 6), 3, fill=(80, 80, 88))
    fx1 = px0 + (px1 - px0) * pos / 228
    d.rounded_rectangle((px0, py, fx1, py + 6), 3, fill=(255, 140, 70))
    d.ellipse((fx1 - 9, py - 6, fx1 + 9, py + 12), fill=(255, 255, 255))
    # プレーヤー内の音量表示
    vx = PW_X + PW_W - 240
    d.text((vx, PW_Y + 490), chr(0xE767), font=ICONS(24), fill=(220, 220, 225), anchor="lm")
    d.rounded_rectangle((vx + 40, PW_Y + 487, vx + 190, PW_Y + 493), 3, fill=(80, 80, 88))
    if True:  # プレーヤー自身の音量は固定（ScrollVolume が変えるのは Windows の音量）
        d.rounded_rectangle((vx + 40, PW_Y + 487, vx + 40 + 150 * 0.8, PW_Y + 493), 3, fill=(255, 140, 70))

    # 領域の光
    z, rng_ = zone_for(t)
    if z:
        a = seg_alpha(t, rng_[0] - 0.2, rng_[1] + 0.4, 0.4)
        pulse = 0.75 + 0.25 * math.sin(t * 5)
        im.alpha_composite(with_alpha(GLOWS[z], a * pulse))

    # トレイの数字
    ti = tray_number(vol, muted)
    im.alpha_composite(ti, (TRAY_ICON[0] - ti.width // 2, TRAY_ICON[1] - ti.height // 2))

    cur = cursor_at(t)
    zone_now = z if z in ("corner", "edge") else "taskbar"

    # 音量バー（最後の操作から 1.2 秒）
    lt = last_tick_before(t)
    last_evt = max([x for x in [lt[0] if lt else -9] + [c for c in MIDDLE_CLICKS if c <= t]])
    since = t - last_evt
    if 0 <= since < 1.35:
        a = min(1.0, (1.35 - since) / 0.25)
        o = osd_image(vol, muted)
        ox, oy = osd_pos(cur, zone_now)
        im.alpha_composite(with_alpha(o, a), (ox, oy))

    # ミキサー
    if t >= MIXER_OPEN:
        k = min(1.0, (t - MIXER_OPEN) / 0.22)
        e = 1 - (1 - k) ** 3
        mxw, mxh = MIXER.size
        x = W - 18 - mxw
        y = WA_BOTTOM - 18 - mxh + int((1 - e) * 40)
        im.alpha_composite(with_alpha(MIXER_SH, e), (x - MIXER_PAD, y - MIXER_PAD))

    # 字幕
    for t0, t1, s in (CAPTIONS_MAIN_EN if EN else CAPTIONS_MAIN):
        a = seg_alpha(t, t0, t1)
        if a > 0:
            c = caption_img(s, "main")
            im.alpha_composite(with_alpha(c, a), ((W - c.width) // 2, 40))
    for t0, t1, s in (CAPTIONS_SUB_EN if EN else CAPTIONS_SUB):
        a = seg_alpha(t, t0, t1)
        if a > 0:
            c = caption_img(s, "sub")
            im.alpha_composite(with_alpha(c, a), ((W - c.width) // 2, 118))

    # マウスの説明マーク
    strength, direction = 0.0, 0
    if lt and t - lt[0] < 0.45:
        strength = 1 - (t - lt[0]) / 0.45; direction = lt[2]
    middle = 0.0
    for c in MIDDLE_CLICKS:
        if 0 <= t - c < 0.5: middle = 1 - (t - c) / 0.5
    left = 0.0
    for c in LEFT_CLICKS:
        if 0 <= t - c < 0.4: left = 1 - (t - c) / 0.4
    show_badge = any(t0 - 0.1 <= t <= t1 + 0.4 for t0, t1, _ in ZONES)
    if show_badge:
        a = max(seg_alpha(t, t0 - 0.1, t1 + 0.4, 0.3) for t0, t1, _ in ZONES)
        b = mouse_badge(direction, strength, middle, left)
        # 音量バーと重ならない場所に置く
        if zone_now == "corner":
            bx_, by_ = W - 40 - b.width, OSD_M + OSD_H + 30
        elif zone_now == "edge":
            bx_, by_ = OSD_M + OSD_W + 24, cur[1] - b.height / 2
        elif z == "tray" or t > 25.0:
            bx_, by_ = cur[0] - 40 - b.width, cur[1] - 30 - b.height
        else:
            bx_, by_ = cur[0] + OSD_W / 2 + 24, WA_BOTTOM - OSD_M - OSD_H / 2 - b.height / 2
        im.alpha_composite(with_alpha(b, a), (int(bx_), int(by_)))
        # カーソルの位置を示す輪（隅ではカーソルがほとんど画面の外になるため）
        ring = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        rr_ = 30 + 4 * math.sin(t * 6)
        ImageDraw.Draw(ring).ellipse((cur[0] - rr_, cur[1] - rr_, cur[0] + rr_, cur[1] + rr_),
                                     outline=(255, 255, 255, int(200 * a)), width=4)
        im.alpha_composite(ring)

    # クリックの波紋
    for c in MIDDLE_CLICKS + LEFT_CLICKS:
        if 0 <= t - c < 0.5:
            k = (t - c) / 0.5
            r = 12 + 40 * k
            ov = Image.new("RGBA", (W, H), (0, 0, 0, 0))
            ImageDraw.Draw(ov).ellipse((cur[0] - r, cur[1] - r, cur[0] + r, cur[1] + r),
                                       outline=ACCENT + (int(255 * (1 - k)),), width=4)
            im.alpha_composite(ov)

    im.alpha_composite(CURSOR_IMG, (int(cur[0]) - CURSOR_HOT, int(cur[1]) - CURSOR_HOT))
    return im

def frame(t):
    if t < TITLE_END - 0.6:
        img = TITLE
    elif t < TITLE_END:
        k = (t - (TITLE_END - 0.6)) / 0.6
        img = Image.blend(TITLE, desktop_frame(t), ease(k))
    elif t < OUTRO_START:
        img = desktop_frame(t)
    elif t < OUTRO_START + 0.7:
        k = (t - OUTRO_START) / 0.7
        img = Image.blend(desktop_frame(t), OUTRO, ease(k))
    else:
        img = OUTRO
    img = img.convert("RGB")
    # 頭と最後の黒フェード
    fade = min(1.0, t / 0.5, (DURATION - t) / 1.0)
    if fade < 1:
        img = Image.blend(Image.new("RGB", (W, H)), img, max(0.0, fade))
    return img

if PREVIEW:
    base = os.path.splitext(OUT)[0]
    # スペクトルの減衰を正しくするため少し前から回す
    for tp in PREVIEW:
        spec_state[:] = 0
        for k in range(6, 0, -1): spectrum(tp - k / FPS)
        frame(tp).save(f"{base}_t{tp:05.2f}.png")
        print("saved", tp)
    sys.exit(0)

cmd = ["ffmpeg", "-v", "error", "-y",
       "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-",
       "-i", AUDIO,
       "-c:v", "libx264", "-preset", "slow", "-crf", "18", "-pix_fmt", "yuv420p", "-tune", "animation",
       "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", OUT]
p = subprocess.Popen(cmd, stdin=subprocess.PIPE)
nf = int(DURATION * FPS)
for i in range(nf):
    t = i / FPS
    p.stdin.write(frame(t).tobytes())
    if i % 60 == 0: print(f"{i}/{nf}", flush=True)
p.stdin.close()
p.wait()
print("done", OUT)
