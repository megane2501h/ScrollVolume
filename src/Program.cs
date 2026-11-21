using System;
using System.Threading;
using System.Windows.Forms;

namespace ScrollVolume
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // 自動テスト: ScrollVolume.exe --test [画像出力フォルダ]
            if (args.Length >= 1 && args[0] == "--test")
            {
                Log.Init();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
                return Tests.Run(args.Length >= 2 ? args[1] : null);
            }

            // 管理者権限のヘルパーとして呼ばれた（設定画面から UAC 経由）
            if (args.Length >= 1 && args[0] == "--install-task") { Log.Init(); return ElevatedTask.Install(); }
            if (args.Length >= 1 && args[0] == "--uninstall-task") { Log.Init(); return ElevatedTask.Uninstall(); }

            bool fromTask = Array.IndexOf(args, ElevatedTask.FromTaskArg) >= 0;

            // 「管理者権限で動かす」がオンなら、登録済みタスク経由で管理者権限で起動し直す（確認画面なし）
            if (!fromTask && !ElevatedTask.IsElevated())
            {
                Log.Init();
                Settings s0 = Settings.Load();
                if (s0.RunElevated && ElevatedTask.Exists() && ElevatedTask.Start()) return 0;
            }

            // タスク経由の起動は、切り替え元のインスタンスが終わるのを少し待つ
            Mutex mutex = SingleInstance.TryAcquire(SingleInstance.MutexName, fromTask ? 15000 : 0);
            if (mutex == null) return 1;
            using (mutex)
            {
                Log.Init();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Write("ThreadException: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Unhandled: " + e.ExceptionObject);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}
