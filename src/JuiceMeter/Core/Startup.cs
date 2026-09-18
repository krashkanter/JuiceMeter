using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace JuiceMeter.Core;

/// <summary>
/// Run-at-login registration.
///
/// The plain Run key cannot launch an elevated process, so when Juice Meter is
/// already running as administrator it registers a scheduled task with
/// HighestAvailable instead. That keeps the CPU power sensors working from the
/// moment you log in without a UAC prompt every boot.
/// </summary>
internal static class Startup
{
    private const string TaskName = "JuiceMeter";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "JuiceMeter";

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled => HasScheduledTask() || HasRunKey();

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                RemoveRunKey();
                RemoveScheduledTask();
                return true;
            }

            // Elevated now means elevated at login, which is what we want.
            if (Sensors.HardwareReader.IsElevated && TryCreateScheduledTask())
            {
                RemoveRunKey();
                return true;
            }

            RemoveScheduledTask();
            return TryCreateRunKey();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not set run-at-login to {enabled}", ex);
            return false;
        }
    }

    // ------------------------------------------------------------ run key

    private static bool HasRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string value && value.Length > 0;
        }
        catch (SecurityException)
        {
            return false;
        }
    }

    private static bool TryCreateRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(RunValueName, $"\"{ExePath}\" --tray");
        Log.Info("Run-at-login enabled via the Run key");
        return true;
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Could not remove the Run key entry", ex);
        }
    }

    // ----------------------------------------------------- scheduled task

    private static bool HasScheduledTask() => RunSchTasks($"/Query /TN \"{TaskName}\"") == 0;

    private static void RemoveScheduledTask()
    {
        if (HasScheduledTask()) RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
    }

    private static bool TryCreateScheduledTask()
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "juicemeter-task.xml");

        try
        {
            // schtasks insists on UTF-16 for task XML.
            File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);

            if (RunSchTasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F") == 0)
            {
                Log.Info("Run-at-login enabled via a scheduled task with highest privileges");
                return true;
            }

            Log.Warn("schtasks refused to create the startup task; falling back to the Run key");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Could not create the startup scheduled task", ex);
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* best effort */ }
        }
    }

    private static string BuildTaskXml()
    {
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var command = SecurityElement.Escape(ExePath);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Juice Meter</Author>
                <Description>Starts Juice Meter when you log in.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
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
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>--tray</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static int RunSchTasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return -1;
            process.WaitForExit(15_000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception ex)
        {
            Log.Error($"schtasks {arguments} failed", ex);
            return -1;
        }
    }
}
