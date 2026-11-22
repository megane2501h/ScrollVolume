# デモ動画の BGM を合成する（オリジナル曲。外部の音源は使わない）
# 120 BPM のチルなハウス。Am7 - Fmaj7 - Cmaj7 - G6 の繰り返し。
import sys
import numpy as np
from scipy.signal import butter, lfilter, sosfilt, fftconvolve
from scipy.io import wavfile

SR = 44100
BPM = 120
BEAT = 60 / BPM
BAR = BEAT * 4
LENGTH = 40.0
N = int(SR * LENGTH)
rng = np.random.default_rng(7)

def midi(m):
    return 440.0 * 2 ** ((m - 69) / 12)

CHORDS = [  # (ベース, パッドの音)
    (45, [57, 60, 64, 67]),   # Am7
    (41, [53, 57, 60, 64]),   # Fmaj7
    (48, [55, 59, 60, 64]),   # Cmaj7
    (43, [55, 59, 62, 64]),   # G6
]
N_BARS = 19

def chord_at_bar(b):
    if b >= 18:
        return CHORDS[0]
    return CHORDS[b % 4]

L = np.zeros(N); R = np.zeros(N)

def add(buf, start, sig, gain=1.0):
    i = int(start * SR)
    if i >= N: return
    j = min(N, i + len(sig))
    buf[i:j] += sig[: j - i] * gain

def add_st(start, sig, gain=1.0, pan=0.0):
    add(L, start, sig, gain * np.sqrt(0.5 * (1 - pan)))
    add(R, start, sig, gain * np.sqrt(0.5 * (1 + pan)))

def lp(x, fc, order=2):
    sos = butter(order, fc / (SR / 2), output="sos")
    return sosfilt(sos, x)

def hp(x, fc, order=2):
    sos = butter(order, fc / (SR / 2), btype="high", output="sos")
    return sosfilt(sos, x)

def bp(x, lo, hi):
    sos = butter(2, [lo / (SR / 2), hi / (SR / 2)], btype="band", output="sos")
    return sosfilt(sos, x)

def saw(f, n, phase=0.0):
    t = np.arange(n) / SR
    return 2 * ((f * t + phase) % 1.0) - 1

# ---- 曲の構成 ----
DRUMS_FROM = 2          # 2 小節目からドラム・ベース
END_BAR = 18            # 最後の和音

kick_times = []
for b in range(DRUMS_FROM, END_BAR):
    for k in range(4):
        kick_times.append(b * BAR + k * BEAT)
kick_times.append(END_BAR * BAR)

# キック
def kick():
    n = int(0.45 * SR); t = np.arange(n) / SR
    f = 48 + 110 * np.exp(-t / 0.035)
    ph = 2 * np.pi * np.cumsum(f) / SR
    s = np.sin(ph) * np.exp(-t / 0.22)
    s += 0.25 * np.exp(-t / 0.004) * rng.standard_normal(n) * 0.3
    return s
K = kick()
for t in kick_times:
    add_st(t, K, 0.9)

# ハイハット（裏拍）とシェイカー（16 分）
def hat(decay):
    n = int(0.15 * SR); t = np.arange(n) / SR
    return hp(rng.standard_normal(n), 7000) * np.exp(-t / decay)
for b in range(DRUMS_FROM, END_BAR):
    for k in range(4):
        add_st(b * BAR + k * BEAT + BEAT / 2, hat(0.045), 0.22, pan=0.25)
        for s16 in (1, 3):
            add_st(b * BAR + k * BEAT + s16 * BEAT / 4, hat(0.018), 0.07, pan=-0.35)

# クラップ（2・4 拍目）
def clap():
    n = int(0.4 * SR); t = np.arange(n) / SR
    noise = bp(rng.standard_normal(n), 900, 3500)
    env = np.exp(-t / 0.11)
    for d in (0.0, 0.011, 0.022):  # 3 回重ねて手拍子らしく
        env = env + (t >= d) * np.exp(-np.maximum(0, t - d) / 0.006) * 0.8
    return noise * env * 0.5
C = clap()
for b in range(DRUMS_FROM + 1, END_BAR):
    for k in (1, 3):
        add_st(b * BAR + k * BEAT, C, 0.28)

# サイドチェイン（キックに合わせてパッド・ベースを沈める）
side = np.ones(N)
tt = np.arange(N) / SR
for t0 in kick_times:
    i = int(t0 * SR); j = min(N, i + int(0.35 * SR))
    d = (np.arange(j - i)) / SR
    side[i:j] = np.minimum(side[i:j], 1 - 0.6 * np.exp(-d / 0.09))

# パッド
padL = np.zeros(N); padR = np.zeros(N)
for b in range(N_BARS):
    root, notes = chord_at_bar(b)
    dur = BAR + 0.6 if b < END_BAR else 6.0
    n = int(dur * SR); t = np.arange(n) / SR
    att = 0.35
    env = np.minimum(1, t / att) * np.clip((dur - t) / 0.6, 0, 1)
    sl = np.zeros(n); sr_ = np.zeros(n)
    for m in notes:
        f = midi(m)
        for det, pan in ((-0.07, -0.8), (0.0, 0.0), (0.07, 0.8)):
            s = saw(f * 2 ** (det / 12), n, rng.random())
            sl += s * np.sqrt(0.5 * (1 - pan)); sr_ += s * np.sqrt(0.5 * (1 + pan))
    sl = lp(sl, 1500) * env; sr_ = lp(sr_, 1500) * env
    i = int(b * BAR * SR); j = min(N, i + n)
    padL[i:j] += sl[: j - i]; padR[i:j] += sr_[: j - i]
padL *= side * 0.05; padR *= side * 0.05

# ベース（裏拍の 8 分）
bass = np.zeros(N)
for b in range(DRUMS_FROM, END_BAR + 1):
    root, _ = chord_at_bar(b)
    pattern = [0.5, 1.5, 2.5, 3.5] if b < END_BAR else [0.0]
    for k, pos in enumerate(pattern):
        m = root + (12 if (b < END_BAR and k == 3 and b % 2 == 1) else 0)
        dur = 0.42 if b < END_BAR else 3.0
        n = int(dur * SR); t = np.arange(n) / SR
        f = midi(m)
        s = lp(saw(f, n), 520) * 0.7 + np.sin(2 * np.pi * f * t) * 0.8
        env = np.minimum(1, t / 0.006) * np.exp(-t / (0.22 if b < END_BAR else 1.2))
        i = int((b * BAR + pos * BEAT) * SR); j = min(N, i + n)
        bass[i:j] += (s * env)[: j - i]
bass *= side * 0.32

# アルペジオ（16 分のプラック、ピンポンディレイ付き）
arpL = np.zeros(N); arpR = np.zeros(N)
ORDER = [0, 1, 2, 3, 2, 3, 1, 2, 0, 2, 1, 3, 2, 1, 3, 2]
for b in range(N_BARS - 1):
    root, notes = chord_at_bar(b)
    tones = [m + 12 for m in notes]
    for s16 in range(16):
        if b < DRUMS_FROM and s16 % 2 == 1:
            continue  # 頭は 8 分で控えめに
        m = tones[ORDER[s16]]
        f = midi(m)
        n = int(0.5 * SR); t = np.arange(n) / SR
        tone = np.sin(2 * np.pi * f * t) + 0.35 * np.sin(2 * np.pi * 2 * f * t) * np.exp(-t / 0.05) \
            + 0.12 * np.sin(2 * np.pi * 3 * f * t) * np.exp(-t / 0.03)
        env = np.minimum(1, t / 0.003) * np.exp(-t / 0.16)
        vel = 0.9 if s16 % 4 == 0 else (0.6 if s16 % 2 == 0 else 0.45)
        pan = -0.35 if s16 % 2 == 0 else 0.35
        start = b * BAR + s16 * BEAT / 4
        sig = tone * env * vel
        i = int(start * SR); j = min(N, i + n)
        arpL[i:j] += sig[: j - i] * np.sqrt(0.5 * (1 - pan))
        arpR[i:j] += sig[: j - i] * np.sqrt(0.5 * (1 + pan))
# ピンポンディレイ（付点 8 分）
dly = int(BEAT * 0.75 * SR)
dL = np.zeros(N); dR = np.zeros(N)
srcL, srcR = arpL.copy(), arpR.copy()
g = 0.45
for k in range(1, 6):
    sh = dly * k
    if sh >= N: break
    if k % 2:
        dR[sh:] += srcL[: N - sh] * g ** k; dL[sh:] += srcR[: N - sh] * g ** k
    else:
        dL[sh:] += srcL[: N - sh] * g ** k; dR[sh:] += srcR[: N - sh] * g ** k
arpL = (arpL + lp(dL, 3000)) * 0.11; arpR = (arpR + lp(dR, 3000)) * 0.11

# リバーブ（減衰するノイズの畳み込み）
ir_n = int(2.2 * SR); ir_t = np.arange(ir_n) / SR
irL = rng.standard_normal(ir_n) * np.exp(-ir_t / 0.55); irR = rng.standard_normal(ir_n) * np.exp(-ir_t / 0.55)
irL = lp(irL, 5000); irR = lp(irR, 5000)
irL /= np.sqrt(np.sum(irL ** 2)); irR /= np.sqrt(np.sum(irR ** 2))
sendL = padL * 0.6 + arpL * 0.8; sendR = padR * 0.6 + arpR * 0.8
revL = fftconvolve(sendL, irL)[:N] * 0.35; revR = fftconvolve(sendR, irR)[:N] * 0.35

L += padL + bass + arpL + revL
R += padR + bass + arpR + revR

# ローカット・軽いソフトクリップ・正規化
L = hp(L, 28); R = hp(R, 28)
mix = np.stack([L, R], axis=1)
mix = np.tanh(mix * 1.2) / 1.2
mix /= np.max(np.abs(mix)) + 1e-9
mix *= 0.9

out = sys.argv[1] if len(sys.argv) > 1 else "bgm_raw.wav"
wavfile.write(out, SR, mix.astype(np.float32))
print("wrote", out, mix.shape)
