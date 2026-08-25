using System.IO;

namespace ZFTP.Core;

/// <summary>
/// Small same-volume replace helper so a crash cannot leave a half-written JSON file.
/// The previous good file is retained as <c>.bak</c> for recovery on next launch.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = path + ".bak";
        try
        {
            File.WriteAllText(temp, contents);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(path, backup, overwrite: true);
                    File.Move(temp, path, overwrite: true);
                }
                catch (IOException)
                {
                    // Antivirus/indexers can briefly prevent Replace; Move with
                    // overwrite still keeps the complete temp file intact.
                    File.Copy(path, backup, overwrite: true);
                    File.Move(temp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
