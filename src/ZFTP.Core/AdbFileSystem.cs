// ============================================================================
//  ZFTP - AdbFileSystem
//  ---------------------------------------------------------------------------
//  The "translator" between Windows and an Android device, the same role
//  SftpFileSystem plays for an SFTP server. WinFsp calls these methods whenever
//  something on the PC touches the Android drive; we turn each call into an adb
//  command via AdbService.
//
//  ADB has no random-access file handles like SFTP does - it can only pull or
//  push whole files (or a slow `dd`). So this engine uses a LOCAL TEMP-FILE
//  CACHE: opening a file pulls it once to a temp file, reads/writes hit that
//  local copy, and a modified file is pushed back when the handle closes. This
//  is the same approach AndroidDrive uses (and what rclone's "vfs-cache full"
//  does for our cloud drives): opening a big file has a one-time copy delay,
//  then it behaves like a normal file.
//
//  Directory listings and metadata come from `adb shell ls -al`.
// ============================================================================

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using Fsp;

using FileInfo = Fsp.Interop.FileInfo;       // WinFsp's file-info struct (NOT System.IO.FileInfo)
using VolumeInfo = Fsp.Interop.VolumeInfo;   // WinFsp's volume-info struct

namespace ZFTP.Core;

/// <summary>A WinFsp filesystem whose storage lives on an Android device (over adb).</summary>
public sealed class AdbFileSystem : FileSystemBase, IDisposable
{
    private readonly string _serial;         // adb device serial
    private readonly string _remoteRoot;     // device path that maps to the drive root, e.g. "/sdcard"
    private readonly string _volumeLabel;    // shown as the drive's name in Explorer
    private readonly bool _readOnly;
    private readonly string _tempDir;        // local cache folder for this mount
    private readonly object _gate = new();    // serializes metadata ops (ls/stat/cache)
    private readonly byte[] _defaultSecurity; // one "everyone full access" descriptor reused everywhere

    // ---- attribute cache (same idea as SftpFileSystem) --------------------
    private const long AttrCacheTtlMs = 15_000;
    private readonly Dictionary<string, (AdbStat attr, long expires)> _attrCache = new();

    // Real volume size, filled in by MountSession from a `df` probe.
    private long _totalBytes = 1L << 42;     // 4 TB placeholder
    private long _freeBytes = 1L << 41;      // 2 TB placeholder

    // Live transfer totals for the speed display.
    private long _bytesRead;
    private long _bytesWritten;
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>One stat'd entry on the device.</summary>
    private readonly struct AdbStat
    {
        public AdbStat(bool isDir, long size, DateTime mtime) { IsDirectory = isDir; Size = size; Mtime = mtime; }
        public bool IsDirectory { get; }
        public long Size { get; }
        public DateTime Mtime { get; }
    }

    /// <summary>One open handle. WinFsp hands this back on every Read/Write/Close.</summary>
    private sealed class FileDesc
    {
        public required string Path;            // device unix path
        public bool IsDirectory;
        public bool ExistsRemote;               // false for a freshly Create()d file (not on the device yet)
        public string? LocalPath;               // temp cache file (created lazily)
        public System.IO.FileStream? Stream;    // open handle on the local cache file
        public bool Pulled;                     // have we fetched the device content yet
        public bool Dirty;                      // local copy changed - push on close
        public readonly object Lock = new();    // guards this handle's stream/cache (NOT the global _gate)
    }

    public AdbFileSystem(string serial, string remoteRoot, string volumeLabel = "ZFTP", bool readOnly = false)
    {
        _serial = serial;
        _remoteRoot = NormalizeRemote(remoteRoot);
        _volumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "ZFTP" : volumeLabel;
        _readOnly = readOnly;

        _tempDir = Path.Combine(Path.GetTempPath(), "ZFTP-adb", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        // Permissive Windows-side security descriptor (same as SftpFileSystem) so
        // Windows never blocks access; what actually succeeds is decided by the
        // device's own permissions.
        string sddl;
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            sddl = $"O:{sid}G:{sid}D:P(A;;FA;;;{sid})(A;;FA;;;WD)(A;;FA;;;AU)(A;;FA;;;SY)(A;;FA;;;BA)";
        }
        catch
        {
            sddl = "O:BAG:BAD:P(A;;FA;;;WD)";
        }
        var sd = new RawSecurityDescriptor(sddl);
        _defaultSecurity = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_defaultSecurity, 0);
    }

    /// <summary>Update the advertised drive size (called with real numbers from `df`).</summary>
    public void UpdateVolumeSpace(long totalBytes, long freeBytes)
    {
        if (totalBytes <= 0) return;
        if (freeBytes < 0) freeBytes = 0;
        if (freeBytes > totalBytes) freeBytes = totalBytes;
        Interlocked.Exchange(ref _totalBytes, totalBytes);
        Interlocked.Exchange(ref _freeBytes, freeBytes);
    }

    // ---- path helpers ------------------------------------------------------

    private static string NormalizeRemote(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "/sdcard";
        p = p.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1 && p.EndsWith('/')) p = p.TrimEnd('/');
        return p;
    }

    /// <summary>Turn a Windows path like "\folder\file.txt" into a device unix path.</summary>
    private string ToRemote(string winPath)
    {
        if (winPath is "\\" or "") return _remoteRoot;
        var rel = winPath.Replace('\\', '/').TrimStart('/');
        return _remoteRoot == "/" ? "/" + rel : _remoteRoot + "/" + rel;
    }

    private static string ParentOf(string remotePath)
    {
        var i = remotePath.LastIndexOf('/');
        return i <= 0 ? "/" : remotePath[..i];
    }

    private static string NameOf(string remotePath)
    {
        var i = remotePath.LastIndexOf('/');
        return i < 0 ? remotePath : remotePath[(i + 1)..];
    }

    // ---- time / info builders ---------------------------------------------

    private static ulong ToFileTime(DateTime dt)
    {
        try { return dt.Year < 1601 ? 0UL : (ulong)dt.ToFileTimeUtc(); }
        catch { return 0UL; }
    }

    private static FileInfo BuildInfo(AdbStat a)
    {
        var info = default(FileInfo);
        info.FileAttributes = a.IsDirectory
            ? (uint)System.IO.FileAttributes.Directory
            : (uint)System.IO.FileAttributes.Normal;
        info.FileSize = a.IsDirectory ? 0UL : (ulong)Math.Max(0, a.Size);
        info.AllocationSize = (info.FileSize + 4095) / 4096 * 4096;
        info.CreationTime = ToFileTime(a.Mtime);
        info.LastAccessTime = ToFileTime(a.Mtime);
        info.LastWriteTime = ToFileTime(a.Mtime);
        info.ChangeTime = info.LastWriteTime;
        info.IndexNumber = 0;
        info.HardLinks = 0;
        return info;
    }

    // ---- attribute cache ---------------------------------------------------
    // These run under _gate (callers hold it).
    private AdbStat? TryGetCachedAttr(string path)
    {
        if (_attrCache.TryGetValue(path, out var e))
        {
            if (e.expires > Environment.TickCount64) return e.attr;
            _attrCache.Remove(path);
        }
        return null;
    }

    private void CacheAttr(string path, AdbStat a) =>
        _attrCache[path] = (a, Environment.TickCount64 + AttrCacheTtlMs);

    private void InvalidateAttr(string path) => _attrCache.Remove(path);

    /// <summary>Stat a single device path with `ls -ld`, using the short-lived cache when we can.</summary>
    private AdbStat GetAttrsCached(string path)
    {
        var cached = TryGetCachedAttr(path);
        if (cached != null) return cached.Value;

        // `ls -ld` lists the entry itself (for a dir, NOT its contents).
        if (!AdbService.Shell(_serial, "ls -ld -- " + AdbService.ShellQuote(path), out var output, TimeSpan.FromSeconds(15)))
            throw new FileNotFoundException(path);

        foreach (var line in output.Split('\n'))
        {
            if (TryParseLsLine(line.TrimEnd('\r'), out var stat, out _))
            {
                CacheAttr(path, stat);
                return stat;
            }
        }
        throw new FileNotFoundException(path);
    }

    // ---- ls -al parsing ----------------------------------------------------
    // Matches a toybox `ls -al` row (modern Android, Android 6+):
    //   drwxrwx--x  3 root sdcard_rw   4096 2024-06-01 12:00 Android
    //   -rw-rw----  1 u0_a1 ext_data 524288 2024-06-01 12:00 movie.mp4
    //   lrwxrwxrwx  1 root root          21 2024-06-01 12:00 sdcard -> /storage/...
    // Groups: 1=type char, 2=size, 3=date, 4=time, 5=name (+ optional "-> target").
    private static readonly Regex LsLine = new(
        @"^([\-dlbcps])[rwxsStT\-]{9}[\.\+]?\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2})\s+(.+)$",
        RegexOptions.Compiled);

    private static bool TryParseLsLine(string line, out AdbStat stat, out string name)
    {
        stat = default;
        name = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        var t = line.Trim();
        if (t.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) return false;

        var m = LsLine.Match(t);
        if (!m.Success) return false;

        char type = m.Groups[1].Value[0];
        long.TryParse(m.Groups[2].Value, out long size);

        DateTime mtime;
        var stamp = m.Groups[3].Value + " " + m.Groups[4].Value;
        if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out mtime))
            mtime = DateTime.MinValue;

        name = m.Groups[5].Value;
        // Symlinks list as "name -> target"; keep just the name and treat as a file.
        if (type == 'l')
        {
            int arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) name = name[..arrow];
        }

        bool isDir = type == 'd';
        stat = new AdbStat(isDir, isDir ? 0 : size, mtime);
        return true;
    }

    // ---- volume ------------------------------------------------------------

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = (ulong)Interlocked.Read(ref _totalBytes);
        volumeInfo.FreeSize = (ulong)Interlocked.Read(ref _freeBytes);
        volumeInfo.SetVolumeLabel(_volumeLabel);
        return STATUS_SUCCESS;
    }

    // ---- path resolution / attributes -------------------------------------

    public override int GetSecurityByName(string fileName, out uint fileAttributes,
        ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        var path = ToRemote(fileName);
        lock (_gate)
        {
            try
            {
                var a = GetAttrsCached(path);
                fileAttributes = a.IsDirectory
                    ? (uint)System.IO.FileAttributes.Directory
                    : (uint)System.IO.FileAttributes.Normal;
                securityDescriptor = _defaultSecurity;
                return STATUS_SUCCESS;
            }
            catch { return STATUS_OBJECT_NAME_NOT_FOUND; }
        }
    }

    // ---- open / create -----------------------------------------------------

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object? fileNode, out object? fileDesc, out FileInfo fileInfo, out string? normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = null;

        var path = ToRemote(fileName);
        lock (_gate)
        {
            try
            {
                var a = GetAttrsCached(path);
                fileDesc = new FileDesc { Path = path, IsDirectory = a.IsDirectory, ExistsRemote = true };
                fileInfo = BuildInfo(a);
                normalizedName = fileName;
                return STATUS_SUCCESS;
            }
            catch { return STATUS_OBJECT_NAME_NOT_FOUND; }
        }
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[] securityDescriptor, ulong allocationSize,
        out object? fileNode, out object? fileDesc, out FileInfo fileInfo, out string? normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = null;

        if (_readOnly) return STATUS_MEDIA_WRITE_PROTECTED;

        var path = ToRemote(fileName);
        bool makeDir = (createOptions & FILE_DIRECTORY_FILE) != 0;

        lock (_gate)
        {
            try
            {
                // Fail if it already exists (matches Windows "create new" semantics).
                try { _ = GetAttrsCached(path); return STATUS_OBJECT_NAME_COLLISION; }
                catch { /* not found - good */ }

                if (makeDir)
                {
                    if (!AdbService.Shell(_serial, "mkdir -p -- " + AdbService.ShellQuote(path), out _, TimeSpan.FromSeconds(20)))
                        return STATUS_ACCESS_DENIED;
                    var dstat = new AdbStat(true, 0, DateTime.Now);
                    CacheAttr(path, dstat);
                    fileDesc = new FileDesc { Path = path, IsDirectory = true, ExistsRemote = true };
                    fileInfo = BuildInfo(dstat);
                }
                else
                {
                    // New empty file: build it in the local cache; it's pushed to the
                    // device when the handle is cleaned up (so even a 0-byte file appears).
                    var desc = new FileDesc { Path = path, IsDirectory = false, ExistsRemote = false, Dirty = true };
                    desc.LocalPath = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
                    File.WriteAllBytes(desc.LocalPath, Array.Empty<byte>());
                    desc.Pulled = true;
                    var fstat = new AdbStat(false, 0, DateTime.Now);
                    CacheAttr(path, fstat);
                    fileDesc = desc;
                    fileInfo = BuildInfo(fstat);
                }
                normalizedName = fileName;
                return STATUS_SUCCESS;
            }
            catch { return STATUS_ACCESS_DENIED; }
        }
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes,
        bool replaceFileAttributes, ulong allocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (_readOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        var d = (FileDesc)fileDesc;
        lock (d.Lock)
        {
            try
            {
                var s = EnsureLocal(d, forWrite: true);
                s.SetLength(0);
                s.Flush();
                d.Dirty = true;
                fileInfo = BuildLocalInfo(d);
                return STATUS_SUCCESS;
            }
            catch { return STATUS_ACCESS_DENIED; }
        }
    }

    // ---- local cache (pull on first touch) --------------------------------

    private System.IO.FileStream EnsureLocal(FileDesc d, bool forWrite)
    {
        // Per-handle lock only: a slow pull/push must NOT freeze the whole drive.
        if (d.Stream != null) return d.Stream;

        d.LocalPath ??= Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        if (!d.Pulled)
        {
            if (d.ExistsRemote)
            {
                if (!AdbService.Pull(_serial, d.Path, d.LocalPath))
                    throw new IOException("adb pull failed for " + d.Path);
            }
            else
            {
                File.WriteAllBytes(d.LocalPath, Array.Empty<byte>());
            }
            d.Pulled = true;
        }
        d.Stream = new System.IO.FileStream(d.LocalPath, FileMode.OpenOrCreate,
            System.IO.FileAccess.ReadWrite, FileShare.ReadWrite);
        return d.Stream;
    }

    private FileInfo BuildLocalInfo(FileDesc d)
    {
        long size = 0;
        try { if (d.Stream != null) size = d.Stream.Length; else if (d.LocalPath != null) size = new System.IO.FileInfo(d.LocalPath).Length; }
        catch { }
        return BuildInfo(new AdbStat(false, size, DateTime.Now));
    }

    // ---- read / write ------------------------------------------------------

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset,
        uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var d = (FileDesc)fileDesc;
        lock (d.Lock)
        {
            try
            {
                var s = EnsureLocal(d, forWrite: false);
                if ((long)offset >= s.Length) return STATUS_END_OF_FILE;

                s.Position = (long)offset;
                var managed = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = s.Read(managed, read, (int)length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read <= 0) return STATUS_END_OF_FILE;

                Marshal.Copy(managed, 0, buffer, read);
                bytesTransferred = (uint)read;
                Interlocked.Add(ref _bytesRead, read);
                return STATUS_SUCCESS;
            }
            catch { return STATUS_UNEXPECTED_IO_ERROR; }
        }
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset,
        uint length, bool writeToEndOfFile, bool constrainedIo,
        out uint bytesTransferred, out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        if (_readOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        var d = (FileDesc)fileDesc;
        lock (d.Lock)
        {
            try
            {
                var s = EnsureLocal(d, forWrite: true);
                long writeAt = writeToEndOfFile ? s.Length : (long)offset;

                if (constrainedIo)
                {
                    if (writeAt >= s.Length) { fileInfo = BuildLocalInfo(d); return STATUS_SUCCESS; }
                    if (writeAt + length > s.Length) length = (uint)(s.Length - writeAt);
                }

                var managed = new byte[length];
                Marshal.Copy(buffer, managed, 0, (int)length);
                s.Position = writeAt;
                s.Write(managed, 0, (int)length);
                s.Flush();
                d.Dirty = true;

                bytesTransferred = length;
                Interlocked.Add(ref _bytesWritten, length);
                fileInfo = BuildLocalInfo(d);
                return STATUS_SUCCESS;
            }
            catch { return STATUS_UNEXPECTED_IO_ERROR; }
        }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        var d = fileDesc as FileDesc;
        if (d == null) return STATUS_SUCCESS;
        lock (d.Lock)
        {
            try { d.Stream?.Flush(); fileInfo = BuildLocalInfo(d); }
            catch { }
            return STATUS_SUCCESS;
        }
    }

    // ---- metadata ----------------------------------------------------------

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        var d = (FileDesc)fileDesc;
        // A file we've modified locally isn't on the device yet - report the local size.
        if (!d.IsDirectory)
        {
            lock (d.Lock)
            {
                if (d.Dirty || (d.Stream != null) || !d.ExistsRemote)
                {
                    fileInfo = BuildLocalInfo(d);
                    return STATUS_SUCCESS;
                }
            }
        }
        lock (_gate)
        {
            try { fileInfo = BuildInfo(GetAttrsCached(d.Path)); return STATUS_SUCCESS; }
            catch { return STATUS_OBJECT_NAME_NOT_FOUND; }
        }
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes,
        ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime,
        out FileInfo fileInfo)
    {
        // adb can't reliably set timestamps without root; accept and move on so
        // Explorer copies don't fail (same best-effort stance as SftpFileSystem).
        fileInfo = default;
        if (_readOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        var d = (FileDesc)fileDesc;
        lock (d.Lock) { try { fileInfo = d.IsDirectory ? BuildInfo(new AdbStat(true, 0, DateTime.Now)) : BuildLocalInfo(d); } catch { } }
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize,
        bool setAllocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (_readOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        var d = (FileDesc)fileDesc;
        lock (d.Lock)
        {
            try
            {
                var s = EnsureLocal(d, forWrite: true);
                s.SetLength((long)newSize);
                s.Flush();
                d.Dirty = true;
                fileInfo = BuildLocalInfo(d);
                return STATUS_SUCCESS;
            }
            catch { return STATUS_ACCESS_DENIED; }
        }
    }

    // ---- delete / rename ---------------------------------------------------

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        if (_readOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        var d = (FileDesc)fileDesc;
        if (!d.IsDirectory) return STATUS_SUCCESS;
        lock (_gate)
        {
            try
            {
                // Non-empty directory? `ls -A` lists everything except . and ..
                if (AdbService.Shell(_serial, "ls -A -- " + AdbService.ShellQuote(d.Path), out var output, TimeSpan.FromSeconds(15)))
                {
                    foreach (var line in output.Split('\n'))
                        if (line.Trim().Length > 0) return STATUS_DIRECTORY_NOT_EMPTY;
                }
                return STATUS_SUCCESS;
            }
            catch { return STATUS_SUCCESS; }
        }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName,
        string newFileName, bool replaceIfExists)
    {
        if (_readOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        var from = ToRemote(fileName);
        var to = ToRemote(newFileName);
        lock (_gate)
        {
            try
            {
                bool targetExists;
                try { _ = GetAttrsCached(to); targetExists = true; } catch { targetExists = false; }
                if (targetExists && !replaceIfExists) return STATUS_OBJECT_NAME_COLLISION;

                // `mv -f` overwrites the target if present.
                if (!AdbService.Shell(_serial,
                        "mv -f -- " + AdbService.ShellQuote(from) + " " + AdbService.ShellQuote(to),
                        out _, TimeSpan.FromSeconds(60)))
                    return STATUS_ACCESS_DENIED;

                InvalidateAttr(from);
                InvalidateAttr(to);
                return STATUS_SUCCESS;
            }
            catch { return STATUS_ACCESS_DENIED; }
        }
    }

    // ---- close / cleanup ---------------------------------------------------

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        var d = (FileDesc)fileDesc;

        // Delete-on-close (how Windows deletes things).
        if (!_readOnly && (flags & CleanupDelete) != 0)
        {
            lock (d.Lock) { CloseStream(d); }
            lock (_gate)
            {
                try
                {
                    var cmd = d.IsDirectory
                        ? "rmdir -- " + AdbService.ShellQuote(d.Path)
                        : "rm -f -- " + AdbService.ShellQuote(d.Path);
                    AdbService.Shell(_serial, cmd, out _, TimeSpan.FromSeconds(30));
                    InvalidateAttr(d.Path);
                }
                catch { }
            }
            return;
        }

        // Push a modified file back to the device when its handle is done.
        if (!_readOnly && !d.IsDirectory)
        {
            lock (d.Lock)
            {
                if (d.Dirty && d.LocalPath != null)
                {
                    try
                    {
                        d.Stream?.Flush();
                        if (AdbService.Push(_serial, d.LocalPath, d.Path))
                        {
                            d.Dirty = false;
                            d.ExistsRemote = true;
                            lock (_gate) InvalidateAttr(d.Path);
                        }
                    }
                    catch { }
                }
            }
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        var d = (FileDesc)fileDesc;
        lock (d.Lock) { CloseStream(d); DeleteLocal(d); }
    }

    private static void CloseStream(FileDesc d)
    {
        if (d.Stream == null) return;
        try { d.Stream.Flush(); d.Stream.Dispose(); } catch { }
        d.Stream = null;
    }

    private static void DeleteLocal(FileDesc d)
    {
        if (d.LocalPath == null) return;
        try { if (File.Exists(d.LocalPath)) File.Delete(d.LocalPath); } catch { }
        d.LocalPath = null;
    }

    // ---- directory listing -------------------------------------------------

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern,
        string marker, ref object context, out string? fileName, out FileInfo fileInfo)
    {
        fileName = null;
        fileInfo = default;

        if (context is not IEnumerator<KeyValuePair<string, FileInfo>> en)
        {
            var entries = new List<KeyValuePair<string, FileInfo>>();
            var d = (FileDesc)fileDesc;
            lock (_gate)
            {
                bool isRoot = d.Path == _remoteRoot;
                if (!isRoot)
                {
                    try { entries.Add(new(".", BuildInfo(GetAttrsCached(d.Path)))); } catch { }
                    try { entries.Add(new("..", BuildInfo(GetAttrsCached(ParentOf(d.Path))))); } catch { }
                }

                if (AdbService.Shell(_serial, "ls -al -- " + AdbService.ShellQuote(d.Path), out var output, TimeSpan.FromSeconds(60)))
                {
                    foreach (var raw in output.Split('\n'))
                    {
                        if (!TryParseLsLine(raw.TrimEnd('\r'), out var stat, out var name)) continue;
                        if (name is "." or ".." or "") continue;
                        entries.Add(new(name, BuildInfo(stat)));
                        // Warm the cache for the per-file stat calls Explorer makes next.
                        var childPath = d.Path == "/" ? "/" + name : d.Path + "/" + name;
                        CacheAttr(childPath, stat);
                    }
                }
            }

            IEnumerable<KeyValuePair<string, FileInfo>> seq = entries;
            if (!string.IsNullOrEmpty(marker))
                seq = entries.SkipWhile(kv => string.CompareOrdinal(kv.Key, marker) <= 0);

            en = seq.GetEnumerator();
            context = en;
        }

        if (!en.MoveNext()) return false;
        fileName = en.Current.Key;
        fileInfo = en.Current.Value;
        return true;
    }

    // ---- security (simplified, same as SftpFileSystem) --------------------

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _defaultSecurity;
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections,
        byte[] securityDescriptor) => STATUS_SUCCESS;

    // ---- teardown ----------------------------------------------------------

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* temp files - OS will reap them eventually */ }
    }
}
