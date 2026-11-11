using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SapphireNotch.Windows;

internal static class GitHubService
{
	public static async Task<string> SignInAsync()
	{
		string executable = Path.Combine(AppContext.BaseDirectory, "Tools", "gh.exe");
		if (!File.Exists(executable))
		{
			return "GitHub helper missing—reinstall Sapphire";
		}
		ProcessStartInfo startInfo = new ProcessStartInfo(executable, "auth login --hostname github.com --git-protocol https --web --clipboard")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		using Process process = Process.Start(startInfo);
		if (process == null)
		{
			return "Could not start GitHub sign-in";
		}
		await process.StandardInput.WriteLineAsync();
		process.StandardInput.Close();
		await process.WaitForExitAsync();
		if (process.ExitCode != 0)
		{
			return "GitHub sign-in was not completed";
		}
		using Process setup = Process.Start(new ProcessStartInfo(executable, "auth setup-git")
		{
			UseShellExecute = false,
			CreateNoWindow = true
		});
		if (setup != null)
		{
			await setup.WaitForExitAsync();
		}
		return "GitHub connected";
	}
}
