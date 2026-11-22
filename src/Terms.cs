namespace ScrollVolume
{
    /// <summary>
    /// 画面に出る用語（日本語・英語）。同じものを指す言葉は必ずここから使い、表記を揺らさない。
    /// 追加・変更したら TERMS.md（用語集）も合わせて直すこと。
    /// </summary>
    static class Terms
    {
        // ---- ScrollVolume のもの ----
        /// <summary>ScrollVolume 独自のミキサー（デバイス × アプリ、メーター付き）。Windows の「音量ミキサー」と区別するため必ず ScrollVolume を付ける。</summary>
        public static string Mixer { get { return L.T("ScrollVolume ミキサー", "ScrollVolume Mixer"); } }
        public static string OpenMixer { get { return L.T("ScrollVolume ミキサーを開く", "Open ScrollVolume Mixer"); } }
        public static string Settings { get { return L.T("ScrollVolume の設定", "ScrollVolume settings"); } }

        // ---- Windows のもの（Windows での呼び名に合わせる）----
        /// <summary>トレイメニューで Windows 側の画面をまとめる見出し。</summary>
        public static string WindowsSound { get { return L.T("Windows のサウンド設定", "Windows sound settings"); } }
        /// <summary>設定アプリのサウンドのページ（ms-settings:sound）。</summary>
        public static string WindowsSoundPage { get { return L.T("設定 > システム > サウンド", "Settings > System > Sound"); } }
        /// <summary>sndvol.exe。</summary>
        public static string WindowsMixer { get { return L.T("音量ミキサー", "Volume mixer"); } }
        public static string OpenWindowsMixer { get { return L.T("Windows の音量ミキサーを開く", "Open Windows volume mixer"); } }
        /// <summary>サウンド コントロール パネル（mmsys.cpl）。設定アプリの「サウンドの詳細設定」から開くものと同じ。</summary>
        public static string WindowsMoreSound { get { return L.T("サウンドの詳細設定", "More sound settings"); } }
        public static string WindowsMoreSoundTip { get { return L.T("再生デバイスの有効・無効、プロパティ", "Enable or disable playback devices, properties"); } }

        // ---- 音量・ミュート・デバイス ----
        public static string Mute { get { return L.T("ミュート", "Mute"); } }
        public static string ToggleMute { get { return L.T("ミュートの切り替え", "Toggle mute"); } }
        public static string PlaybackDevice { get { return L.T("再生デバイス", "Playback device"); } }
        public static string NextDevice { get { return L.T("再生デバイスの切り替え", "Next playback device"); } }
        public static string NoDevice { get { return L.T("再生デバイスがありません", "No playback device"); } }
        public static string DefaultDevice { get { return L.T("既定のデバイス", "Default device"); } }
        public static string SetDefault { get { return L.T("既定にする", "Set as default"); } }
        public static string SystemSounds { get { return L.T("システム音", "System sounds"); } }
        public static string VolumeBar { get { return L.T("音量バー", "Volume bar"); } }

        /// <summary>「音量: 42%」「音量: 42%（ミュート）」「音量: 再生デバイスがありません」。トレイのツールチップとメニューで共通。</summary>
        public static string VolumeLine(int pct, bool muted)
        {
            if (pct < 0) return L.T("音量: ", "Volume: ") + (L.IsJapanese ? NoDevice : NoDevice.ToLowerInvariant());
            return L.T("音量: ", "Volume: ") + pct + "%" + (muted ? L.T("（ミュート）", " (muted)") : "");
        }
    }
}
