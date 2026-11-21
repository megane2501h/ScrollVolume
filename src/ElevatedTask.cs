using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ScrollVolume
{
    /// <summary>
    /// 管理者権限での起動。
    /// 管理者として動いているアプリが前面にあると、通常権限のアプリには Windows の仕様でマウス操作が届かない。
    /// そこでタスクスケジューラに「最上位の特権で実行」のタスクを登録し、確認画面なしで管理者権限で起動できるようにする。
    /// タスクには起動トリガーを付けない（自動起動は従来どおり Run キー → 通常権限で起動 → このタスクを実行して自分は終了）。
    /// </summary>
    static class ElevatedTask
    {
        public const string TaskName = "ScrollVolume_Elevated";
        public const string FromTaskArg = "--from-task";
        static string Schtasks { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"); } }

        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static int RunSchtasks(string args, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Schtasks, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (Process p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }

        public static bool Exists()
        {
            string o;
            return RunSchtasks("/query /tn \"" + TaskName + "\"", out o) == 0;
        }

        public static bool Start()
        {
            string o;
            int code = RunSchtasks("/run /tn \"" + TaskName + "\"", out o);
            if (code != 0) Log.Write("schtasks /run failed (" + code + "): " + o.Trim());
            return code == 0;
        }

        static string X(string s) { return SecurityElement.Escape(s); }

        public static string BuildXml(string exePath, string userId)
        {
            return
@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>ScrollVolume を管理者として起動します。ScrollVolume の設定画面から登録・削除されます。</Description>
  </RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <UserId>" + X(userId) + @"</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>" + X(exePath) + @"</Command>
      <Arguments>" + FromTaskArg + @"</Arguments>
      <WorkingDirectory>" + X(Path.GetDirectoryName(exePath)) + @"</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        }

        /// <summary>（管理者権限のヘルパーとして呼ばれる）タスクを登録する。</summary>
        public static int Install()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ScrollVolume_task_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                string user = WindowsIdentity.GetCurrent().Name;
                File.WriteAllText(tmp, BuildXml(Application.ExecutablePath, user), Encoding.Unicode);
                string o;
                int code = RunSchtasks("/create /tn \"" + TaskName + "\" /xml \"" + tmp + "\" /f", out o);
                Log.Write("install task: " + code + " " + o.Trim());
                return code;
            }
            catch (Exception ex) { Log.Write("install task failed: " + ex.Message); return 1; }
            finally { try { File.Delete(tmp); } catch { } }
        }

        /// <summary>（管理者権限のヘルパーとして呼ばれる）タスクを削除する。</summary>
        public static int Uninstall()
        {
            string o;
            int code = RunSchtasks("/delete /tn \"" + TaskName + "\" /f", out o);
            Log.Write("uninstall task: " + code + " " + o.Trim());
            return code;
        }

        /// <summary>自分自身を管理者権限で起動して登録・削除させる（UAC の確認画面が出る）。キャンセルされたら false。</summary>
        public static bool RunHelper(string arg, out string error)
        {
            error = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, arg) { UseShellExecute = true, Verb = "runas" };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(60000);
                    if (!p.HasExited) { error = "処理がタイムアウトしました。"; return false; }
                    if (p.ExitCode != 0) { error = "エラーが発生しました（コード: " + p.ExitCode + "）。"; return false; }
                    return true;
                }
            }
            catch (Win32Exception ex)
            {
                error = ex.NativeErrorCode == 1223 ? "ユーザー アカウント制御の確認がキャンセルされました。" : ex.Message;
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }

    /// <summary>多重起動防止。権限の違うインスタンス同士でも正しく判定する。</summary>
    static class SingleInstance
    {
        public const string MutexName = @"Local\ScrollVolume_SingleInstance";

        /// <summary>
        /// 取得できたら Mutex を返す（プロセス終了まで保持すること）。取れなければ null。
        /// waitMs: 前のインスタンスの終了を待つ時間（管理者権限への切り替え直後など）。
        /// </summary>
        public static Mutex TryAcquire(string name, int waitMs)
        {
            Mutex m;
            try { m = new Mutex(false, name); }
            catch (UnauthorizedAccessException) { return null; } // 管理者権限のインスタンスが動いている
            try
            {
                if (m.WaitOne(waitMs)) return m;
            }
            catch (AbandonedMutexException) { return m; } // 前のインスタンスが解放せずに終了した → 取得できている
            m.Dispose();
            return null;
        }
    }
}
