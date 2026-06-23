// ============================================================================
//  ZFTP.Updater — standalone updater.
//  Uses the shared ZFTP.Core updater, which checks GitHub Releases and the CDN
//  and offers whichever is newest. Shows simple message boxes (no console).
// ============================================================================

using System.Diagnostics;
using System.Windows.Forms;
using ZFTP.Core;

const string Title = "ZFTP Updater";

// Current version = the version of ZFTP.exe sitting next to us.
string current = "0.0.0";
try
{
    var exe = Path.Combine(AppContext.BaseDirectory, "ZFTP.exe");
    if (File.Exists(exe))
        current = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? current;
}
catch { /* keep default */ }

try
{
    var result = await Updater.CheckAsync(current);
    switch (result.Status)
    {
        case UpdateCheckStatus.UpToDate:
            Info($"ZFTP is up to date (version {current}).");
            break;

        case UpdateCheckStatus.CouldNotCheck:
            Info("Couldn't find any update info.\n\n" +
                 "Make sure a GitHub release exists at github.com/BallisticOK/ZFTP, " +
                 "or that the CDN (cdn.ballisticok.xyz) is reachable with a valid certificate.");
            break;

        case UpdateCheckStatus.UpdateAvailable:
            var info = result.Info!;
            var ask = MessageBox.Show(
                $"ZFTP {info.Version} is available (you have {current}) — from {info.Source}.\n\n" +
                $"{info.Notes}\n\nDownload and install it now?",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (ask == DialogResult.Yes)
            {
                var path = await Updater.DownloadInstallerAsync(info.Url);
                if (path != null)
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else
                    Info("Download failed. Try again later.");
            }
            break;
    }
}
catch (Exception ex)
{
    Info("Update check failed.\n\n" + ex.Message);
}

static void Info(string message) =>
    MessageBox.Show(message, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
