using System.Diagnostics;
using ProgramManager.Core;

namespace ProgramManager;

internal static class GitHubCredentials
{
    public static async Task<GitHubApi> CreateAsync(UserSettings settings, CancellationToken token)
    {
        if (settings.GitHubTokenProtected.Length > 0) return new GitHubApi(Secrets.Unprotect(settings.GitHubTokenProtected));
        if (!settings.UseGitHubCli) return new GitHubApi();
        // Ask the user's existing CLI login; the credential stays in host memory.
        using var process = new Process { StartInfo = new ProcessStartInfo("gh.exe", "auth token --hostname github.com") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true } };
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            var exit = Task.Run(() => process.WaitForExit());
            using var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var delay = Task.Delay(TimeSpan.FromSeconds(10), delayCancel.Token);
            if (await Task.WhenAny(exit, delay) != exit)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("GitHub 로그인 확인이 지연됐습니다. GitHub 설정에서 로그인 방식을 확인하세요.");
            }
            delayCancel.Cancel();
            await errors;
            var credential = (await output).Trim();
            if (process.ExitCode != 0 || credential.Length == 0) throw new InvalidOperationException("GitHub CLI 로그인이 필요합니다. gh auth login을 실행하거나 GitHub 설정에서 토큰 또는 공개 저장소 방식을 선택하세요.");
            return new GitHubApi(credential);
        }
        catch (System.ComponentModel.Win32Exception)
        { throw new InvalidOperationException("GitHub CLI를 찾지 못했습니다. GitHub 설정에서 토큰 또는 공개 저장소 방식을 선택하세요."); }
    }
}
