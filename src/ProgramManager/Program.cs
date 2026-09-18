using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ProgramManager;

internal static class Program
{
    public const string Version = "0.5.0";
    public const string DisplayName = "Program Manager " + Version;
    public static string DataDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProgramManager");
    public static uint ShowMessage { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
#if NET48
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
#else
        ApplicationConfiguration.Initialize();
#endif
        try
        {
            var index = Array.IndexOf(args, "--data-dir");
            if (index >= 0)
            {
                if (index + 1 >= args.Length) throw new ArgumentException("--data-dir 뒤에 폴더 경로가 필요합니다.");
                DataDirectory = Path.GetFullPath(args[index + 1]);
            }
            Directory.CreateDirectory(DataDirectory);
            using var hash = SHA256.Create();
            var key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant()))).Replace("-", "").Substring(0, 16);
            ShowMessage = RegisterWindowMessage("ProgramManager.Show." + key);
            using var instance = new Mutex(true, "Local\\ProgramManager_" + key, out var created);
            if (!created) { PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero); return; }
            Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            using var form = new MainForm(new AppState(DataDirectory), args.Contains("--tray"));
            Application.Run(form);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"프로그램을 시작하지 못했습니다. 기존 설정 파일은 보존됩니다.\n\n{ex.Message}", DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
