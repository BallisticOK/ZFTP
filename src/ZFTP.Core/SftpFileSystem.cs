// ============================================================================
//  ZFTP — SftpFileSystem
//  ---------------------------------------------------------------------------
//  This class is the "translator" that sits between Windows and your remote
//  server. WinFsp calls these methods whenever something on the PC touches the
//  Z: drive (Explorer listing a folder, Notepad saving a file, etc.). We turn
//  each of those calls into an SFTP request using the SSH.NET library.
//
//  WinFsp talks to us through the base class Fsp.FileSystemBase. We override
//  the operations we care about. Every method returns an NTSTATUS code
//  (0 == STATUS_SUCCESS). The named STATUS_* / Cleanup* constants come from
//  the base class.
// ============================================================================

using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using Renci.SshNet;
using Renci.SshNet.Sftp;

// These two names exist in more than one namespace, so we pin them down:
using FileInfo = Fsp.Interop.FileInfo;       // WinFsp's file-info struct (NOT System.IO.FileInfo)
using VolumeInfo = Fsp.Interop.VolumeInfo;   // WinFsp's volume-info struct

namespace ZFTP.Core;

/// <summary>
/// A WinFsp filesystem whose storage lives on a remote SFTP server.
/// </summary>
public sealed class SftpFileSystem : FileSystemBase
{
    // Note: FILE_DIRECTORY_FILE (the CreateOptions bit meaning "make a
    // directory, not a file") is inherited from FileSystemBase.

    private readonly SftpClient _client;
    private readonly string _remoteRoot;     // remote path that maps to the drive's root, e.g. "/home/me"
    private readonly string _volumeLabel;    // shown as the drive's name in Explorer
    private readonly object _gate = new();    // SSH.NET uses one channel; serialize access to be safe
    private readonly byte[] _defaultSecurity; // one "everyone full access" descriptor reused for every file

    // ---- attribute cache --------------------------------------------------
    // Listing a folder already pulls every entry's attributes. Without a cache,
    // Windows then turns around and asks us for each file's attributes again
    // (GetSecurityByName → Open → GetFileInfo), and each of those is a separate
    // round-trip to the server. Over a slow link that's what makes browsing
    // crawl. We stash the attributes from the listing for a few seconds so those
    // follow-up calls are answered locally instead of over the wire. The cache
    // is invalidated whenever WE change a file, so our own edits are never stale.
    private const long AttrCacheTtlMs = 15_000;
    private readonly Dictionary<string, (SftpFileAttributes attr, long expires)> _attrCache = new();

    // Real volume size, filled in by MountSession from a `df` probe. Defaults to
    // a large fixed size until (and unless) we learn the truth from the server.
    private long _totalBytes = 1L << 42;     // 4 TB placeholder
    private long _freeBytes = 1L << 41;      // 2 TB placeholder

    /// <summary>Update the advertised drive size (called with real numbers from `df`).</summary>
    public void UpdateVolumeSpace(long totalBytes, long freeBytes)
    {
        if (totalBytes <= 0) return;
        if (freeBytes < 0) freeBytes = 0;
        if (freeBytes > totalBytes) freeBytes = totalBytes;
        Interlocked.Exchange(ref _totalBytes, totalBytes);
        Interlocked.Exchange(ref _freeBytes, freeBytes);
    }

    // These run only under _gate (the callers already hold it).
    private SftpFileAttributes? TryGetCachedAttr(string path)
    {
        if (_attrCache.TryGetValue(path, out var e))
        {
            if (e.expires > Environment.TickCount64) return e.attr;
            _attrCache.Remove(path);
        }
        return null;
    }

    private void CacheAttr(string path, SftpFileAttributes a) =>
        _attrCache[path] = (a, Environment.TickCount64 + AttrCacheTtlMs);

    private void InvalidateAttr(string path) => _attrCache.Remove(path);

    /// <summary>Fetch attributes, using the short-lived cache when we can.</summary>
    private SftpFileAttributes GetAttrsCached(string path)
    {
        var cached = TryGetCachedAttr(path);
        if (cached != null) return cached;
        var a = _client.GetAttributes(path);
        CacheAttr(path, a);
        return a;
    }

    /// <summary>Re-read attributes fresh (after we changed the file) and refresh the cache.</summary>
    private FileInfo BuildFreshAndCache(string path)
    {
        var a = _client.GetAttributes(path);
        CacheAttr(path, a);
        return BuildInfo(a);
    }

    // Running totals so the app can show live transfer speed. Updated with
    // Interlocked because WinFsp may call Read/Write from several threads.
    private long _bytesRead;
    private long _bytesWritten;
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>One open handle. WinFsp hands this back to us on every Read/Write/Close.</summary>
    private sealed class FileDesc
    {
        public required string Path;            // remote unix path
        public bool IsDirectory;
        public SftpFileStream? Stream;          // opened lazily on first read/write
        public bool StreamIsWritable;
    }

    private readonly bool _readOnly;

    public SftpFileSystem(SftpClient client, string remoteRoot, string volumeLabel = "ZFTP", bool readOnly = false)
    {
        _client = client;
        _remoteRoot = NormalizeRemote(remoteRoot);
        _volumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "ZFTP" : volumeLabel;
        _readOnly = readOnly;

        // Build a permissive security descriptor so Windows never blocks access:
        // FULL control for the current user, Everyone, Authenticated Users,
        // SYSTEM and Administrators. (Whether a write actually succeeds is then
        // decided by the SFTP server's own file permissions.)
        string sddl;
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            sddl = $"O:{sid}G:{sid}D:P(A;;FA;;;{sid})(A;;FA;;;WD)(A;;FA;;;AU)(A;;FA;;;SY)(A;;FA;;;BA)";
        }
        catch
        {
            sddl = "O:BAG:BAD:P(A;;FA;;;WD)";   // fallback: Everyone full control
        }

        var sd = new RawSecurityDescriptor(sddl);
        _defaultSecurity = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_defaultSecurity, 0);
    }

    // ---- path helpers ------------------------------------------------------

    private static string NormalizeRemote(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "/";
        p = p.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1 && p.EndsWith('/')) p = p.TrimEnd('/');
        return p;
    }

    /// <summary>Turn a Windows path like "\folder\file.txt" into a remote unix path.</summary>
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

    // ---- time helper -------------------------------------------------------

    private static ulong ToFileTime(DateTime dt)
    {
        try { return dt.Year < 1601 ? 0UL : (ulong)dt.ToFileTimeUtc(); }
        catch { return 0UL; }
    }

    /// <summary>Fill a WinFsp FileInfo struct from SFTP attributes.</summary>
    private static FileInfo BuildInfo(ISftpFile file)
    {
        var a = file.Attributes;
        return BuildInfo(a);
    }

    private static FileInfo BuildInfo(SftpFileAttributes a)
    {
        var info = default(FileInfo);
        info.FileAttributes = a.IsDirectory
            ? (uint)System.IO.FileAttributes.Directory
            : (uint)System.IO.FileAttributes.Normal;
        info.FileSize = a.IsDirectory ? 0UL : (ulong)Math.Max(0, a.Size);
        info.AllocationSize = (info.FileSize + 4095) / 4096 * 4096; // round up to 4 KB
        info.CreationTime = ToFileTime(a.LastWriteTime);
        info.LastAccessTime = ToFileTime(a.LastAccessTime);
        info.LastWriteTime = ToFileTime(a.LastWriteTime);
        info.ChangeTime = info.LastWriteTime;
        info.IndexNumber = 0;
        info.HardLinks = 0;
        return info;
    }

    // ---- volume ------------------------------------------------------------

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        // Real size from the server's `df` (filled in by MountSession). Falls back
        // to a large placeholder if the server didn't let us run df.
        volumeInfo.TotalSize = (ulong)Interlocked.Read(ref _totalBytes);
        volumeInfo.FreeSize = (ulong)Interlocked.Read(ref _freeBytes);
        volumeInfo.SetVolumeLabel(_volumeLabel);     // shows as the drive name in Explorer
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
            catch
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }
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
                var desc = new FileDesc { Path = path, IsDirectory = a.IsDirectory };
                fileDesc = desc;
                fileInfo = BuildInfo(a);
                normalizedName = fileName;
                return STATUS_SUCCESS;
            }
            catch
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }
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
                try { _client.GetAttributes(path); return STATUS_OBJECT_NAME_COLLISION; }
                catch { /* not found — good, we can create it */ }

                if (makeDir)
                {
                    _client.CreateDirectory(path);
                }
                else
                {
                    using var s = _client.Create(path); // creates an empty file
                }

                var a = _client.GetAttributes(path);
                CacheAttr(path, a);
                var desc = new FileDesc { Path = path, IsDirectory = makeDir };
                fileDesc = desc;
                fileInfo = BuildInfo(a);
                normalizedName = fileName;
                return STATUS_SUCCESS;
            }
            catch
            {
                return STATUS_ACCESS_DENIED;
            }
        }
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes,
        bool replaceFileAttributes, ulong allocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (_readOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        var d = (FileDesc)fileDesc;
        lock (_gate)
        {
            try
            {
                CloseStream(d);
                using (var s = _client.Create(d.Path)) { } // truncate to zero bytes
                fileInfo = BuildFreshAndCache(d.Path);
                return STATUS_SUCCESS;
            }
            catch { return STATUS_ACCESS_DENIED; }
        }
    }

    // ---- read / write ------------------------------------------------------

    private void EnsureStream(FileDesc d, bool needWrite)
    {
        if (d.Stream != null && (!needWrite || d.StreamIsWritable)) return;
        CloseStream(d);
        try
        {
            d.Stream = _client.Open(d.Path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite);
            d.StreamIsWritable = true;
        }
        catch
        {
            // Server may not allow write — fall back to read-only.
            d.Stream = _client.Open(d.Path, System.IO.FileMode.Open, System.IO.FileAccess.Read);
            d.StreamIsWritable = false;
        }
    }

    private static void CloseStream(FileDesc d)
    {
        if (d.Stream == null) return;
        try { d.Stream.Flush(); d.Stream.Dispose(); } catch { }
        d.Stream = null;
        d.StreamIsWritable = false;
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset,
        uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var d = (FileDesc)fileDesc;
        lock (_gate)
        {
            try
            {
                EnsureStream(d, needWrite: false);
                var s = d.Stream!;
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
        lock (_gate)
        {
            try
            {
                EnsureStream(d, needWrite: true);
                if (!d.StreamIsWritable) return STATUS_ACCESS_DENIED;
                var s = d.Stream!;

                long writeAt = writeToEndOfFile ? s.Length : (long)offset;

                if (constrainedIo)
                {
                    // Must not grow the file. Clamp to what's already there.
                    if (writeAt >= s.Length) { fileInfo = BuildFreshAndCache(d.Path); return STATUS_SUCCESS; }
                    if (writeAt + length > s.Length) length = (uint)(s.Length - writeAt);
                }

                var managed = new byte[length];
                Marshal.Copy(buffer, managed, 0, (int)length);
                s.Position = writeAt;
                s.Write(managed, 0, (int)length);
                s.Flush();

                bytesTransferred = length;
                Interlocked.Add(ref _bytesWritten, length);
                fileInfo = BuildFreshAndCache(d.Path);
                return STATUS_SUCCESS;
            }
            catch { return STATUS_UNEXPECTED_IO_ERROR; }
        }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        var d = fileDesc as FileDesc;
        lock (_gate)
        {
            try
            {
                if (d?.Stream != null) d.Stream.Flush();
                if (d != null) fileInfo = BuildFreshAndCache(d.Path);
                return STATUS_SUCCESS;
            }
            catch { return STATUS_SUCCESS; }
        }
    }

    // ---- metadata ----------------------------------------------------------

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        var d = (FileDesc)fileDesc;
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
        fileInfo = default;
        if (_readOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        var d = (FileDesc)fileDesc;
        lock (_gate)
        {
            try
            {
                var a = _client.GetAttributes(d.Path);
                if (lastWriteTime != 0) a.LastWriteTime = DateTime.FromFileTimeUtc((long)lastWriteTime);
                if (lastAccessTime != 0) a.LastAccessTime = DateTime.FromFileTimeUtc((long)lastAccessTime);
                _client.SetAttributes(d.Path, a);
                fileInfo = BuildFreshAndCache(d.Path);
                return STATUS_SUCCESS;
            }
            catch { return STATUS_SUCCESS; } // best-effort; don't fail the operation
        }
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize,
        bool setAllocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (_readOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        var d = (FileDesc)fileDesc;
        lock (_gate)
        {
            try
            {
                EnsureStream(d, needWrite: true);
                d.Stream!.SetLength((long)newSize);
                d.Stream.Flush();
                fileInfo = BuildFreshAndCache(d.Path);
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
        lock (_gate)
        {
            try
            {
                if (d.IsDirectory)
                {
                    foreach (var e in _client.ListDirectory(d.Path))
                        if (e.Name != "." && e.Name != "..") return STATUS_DIRECTORY_NOT_EMPTY;
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
                try
                {
                    _client.GetAttributes(to);
                    if (!replaceIfExists) return STATUS_OBJECT_NAME_COLLISION;
                    _client.DeleteFile(to); // make room for the rename
                }
                catch { /* target doesn't exist — fine */ }

                _client.RenameFile(from, to);
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
        lock (_gate)
        {
            // WinFsp tells us via the Delete flag that this handle should remove
            // the file/dir when it's closed (that's how Windows deletes things).
            if (!_readOnly && (flags & CleanupDelete) != 0)
            {
                try
                {
                    CloseStream(d);
                    if (d.IsDirectory) _client.DeleteDirectory(d.Path);
                    else _client.DeleteFile(d.Path);
                    InvalidateAttr(d.Path);
                }
                catch { }
            }
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        var d = (FileDesc)fileDesc;
        lock (_gate) { CloseStream(d); }
    }

    // ---- directory listing -------------------------------------------------

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern,
        string marker, ref object context, out string? fileName, out FileInfo fileInfo)
    {
        fileName = null;
        fileInfo = default;

        // First call for this listing: fetch the directory once and stash an
        // enumerator in 'context'. Later calls just advance it.
        if (context is not IEnumerator<KeyValuePair<string, FileInfo>> en)
        {
            var entries = new List<KeyValuePair<string, FileInfo>>();
            var d = (FileDesc)fileDesc;
            lock (_gate)
            {
                bool isRoot = d.Path == _remoteRoot;
                if (!isRoot)
                {
                    try { entries.Add(new(".", BuildFreshAndCache(d.Path))); } catch { }
                    try { entries.Add(new("..", BuildInfo(_client.GetAttributes(ParentOf(d.Path))))); } catch { }
                }
                foreach (var e in _client.ListDirectory(d.Path))
                {
                    if (e.Name is "." or "..") continue;
                    entries.Add(new(e.Name, BuildInfo(e)));
                    // Warm the cache so the per-file stat calls Explorer makes
                    // right after this listing don't each hit the network.
                    var childPath = d.Path == "/" ? "/" + e.Name : d.Path + "/" + e.Name;
                    CacheAttr(childPath, e.Attributes);
                }
            }

            // Resume after 'marker' if WinFsp is continuing a previous buffer.
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

    // ---- security (simplified) ---------------------------------------------

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _defaultSecurity;
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections,
        byte[] securityDescriptor) => STATUS_SUCCESS; // ignored for now
}
