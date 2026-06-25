// ============================================================================
//  ZFTP - AfcFileSystem
//  ---------------------------------------------------------------------------
//  The "translator" between Windows and an Apple device, the iPhone/iPad
//  counterpart to SftpFileSystem. WinFsp calls these methods whenever something
//  on the PC touches the drive; we turn each into an Apple File Conduit (AFC)
//  operation via the imobiledevice-net library.
//
//  Unlike adb, AFC gives real random-access file handles (open/seek/read/write),
//  so this engine reads and writes directly - no whole-file temp cache needed.
//
//  SCOPE (read the class header in AppleDeviceService too): AFC only exposes the
//  device's media area - photos/videos and the Documents of File-Sharing apps -
//  NOT the whole filesystem. Some timestamp/truncate operations aren't supported
//  and are treated as best-effort.
// ============================================================================

using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using iMobileDevice;
using iMobileDevice.Afc;
using iMobileDevice.iDevice;

using FileInfo = Fsp.Interop.FileInfo;       // WinFsp's file-info struct
using VolumeInfo = Fsp.Interop.VolumeInfo;   // WinFsp's volume-info struct

namespace ZFTP.Core;

/// <summary>A WinFsp filesystem whose storage lives on an Apple device (over AFC).</summary>
public sealed class AfcFileSystem : FileSystemBase, IDisposable
{
    private readonly string _remoteRoot;     // AFC path that maps to the drive root ("/" = media root)
    private readonly string _volumeLabel;
    private readonly bool _readOnly;
    private readonly object _gate = new();    // AFC is a single connection - serialize every call
    private readonly byte[] _defaultSecurity;

    private readonly IAfcApi _afcApi;
    private iDeviceHandle? _device;
    private AfcClientHandle? _afc;

    private const long AttrCacheTtlMs = 15_000;
    private readonly Dictionary<string, (AfcStat attr, long expires)> _attrCache = new();

    private long _totalBytes = 1L << 36;     // 64 GB placeholder until we learn the real size
    private long _freeBytes = 1L << 34;      // 16 GB placeholder

    private long _bytesRead;
    private long _bytesWritten;
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    private readonly struct AfcStat
    {
        public AfcStat(bool isDir, long size, DateTime mtime) { IsDirectory = isDir; Size = size; Mtime = mtime; }
        public bool IsDirectory { get; }
        public long Size { get; }
        public DateTime Mtime { get; }
    }

    /// <summary>One open handle. WinFsp hands this back on every Read/Write/Close.</summary>
    private sealed class FileDesc
    {
        public required string Path;
        public bool IsDirectory;
        public ulong Handle;        // AFC file handle (0 = not open)
        public bool Open;
        public bool Writable;
    }

    public AfcFileSystem(string udid, string remoteRoot, string volumeLabel = "ZFTP", bool readOnly = false)
    {
        AppleDeviceService.EnsureLoaded();
        _remoteRoot = NormalizeRemote(remoteRoot);
        _volumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "ZFTP" : volumeLabel;
        _readOnly = readOnly;

        var idevice = LibiMobileDevice.Instance.iDevice;
        _afcApi = LibiMobileDevice.Instance.Afc;

        if (idevice.idevice_new(out _device, udid) != iDeviceError.Success || _device == null)
            throw new InvalidOperationException(
                "Couldn't open the Apple device. Make sure it's plugged in and unlocked.");

        // afc_client_start_service does the lockdown handshake + starts com.apple.afc.
        // It fails until the user taps "Trust This Computer" on the phone.
        var err = _afcApi.afc_client_start_service(_device, out _afc, "ZFTP");
        if (err != AfcError.Success || _afc == null)
            throw new InvalidOperationException(
                "Couldn't connect to the device's file service. Unlock the phone and tap " +
                "\"Trust This Computer\" when prompted, then try again. (If you've never " +
                "connected an iPhone to this PC, install Apple Devices / iTunes so the USB " +
                "driver is present.)");

        // Permissive Windows-side security descriptor (same as the other engines).
        string sddl;
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            sddl = $"O:{sid}G:{sid}D:P(A;;FA;;;{sid})(A;;FA;;;WD)(A;;FA;;;AU)(A;;FA;;;SY)(A;;FA;;;BA)";
        }
        catch { sddl = "O:BAG:BAD:P(A;;FA;;;WD)"; }
        var sd = new RawSecurityDescriptor(sddl);
        _defaultSecurity = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_defaultSecurity, 0);

        RefreshDiskSpace();
    }

    public void UpdateVolumeSpace(long totalBytes, long freeBytes)
    {
        if (totalBytes <= 0) return;
        if (freeBytes < 0) freeBytes = 0;
        if (freeBytes > totalBytes) freeBytes = totalBytes;
        Interlocked.Exchange(ref _totalBytes, totalBytes);
        Interlocked.Exchange(ref _freeBytes, freeBytes);
    }

    /// <summary>Ask the device for its real media-storage size/free.</summary>
    public void RefreshDiskSpace()
    {
        try
        {
            lock (_gate)
            {
                if (_afc == null) return;
                long total = 0, free = 0;
                if (_afcApi.afc_get_device_info_key(_afc, "FSTotalBytes", out var t) == AfcError.Success)
                    long.TryParse(t, out total);
                if (_afcApi.afc_get_device_info_key(_afc, "FSFreeBytes", out var f) == AfcError.Success)
                    long.TryParse(f, out free);
                if (total > 0) UpdateVolumeSpace(total, free);
            }
        }
        catch { /* keep placeholder */ }
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

    // ---- time / info -------------------------------------------------------

    private static ulong ToFileTime(DateTime dt)
    {
        try { return dt.Year < 1601 ? 0UL : (ulong)dt.ToFileTimeUtc(); }
        catch { return 0UL; }
    }

    private static FileInfo BuildInfo(AfcStat a)
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
        return info;
    }

    // ---- attribute cache + stat -------------------------------------------
    private AfcStat? TryGetCachedAttr(string path)
    {
        if (_attrCache.TryGetValue(path, out var e))
        {
            if (e.expires > Environment.TickCount64) return e.attr;
            _attrCache.Remove(path);
        }
        return null;
    }

    private void CacheAttr(string path, AfcStat a) =>
        _attrCache[path] = (a, Environment.TickCount64 + AttrCacheTtlMs);

    private void InvalidateAttr(string path) => _attrCache.Remove(path);

    /// <summary>Stat one path via afc_get_file_info (caller holds _gate).</summary>
    private AfcStat GetAttrsCached(string path)
    {
        var cached = TryGetCachedAttr(path);
        if (cached != null) return cached.Value;

        if (_afc == null) throw new InvalidOperationException("not connected");
        if (_afcApi.afc_get_file_info(_afc, path, out ReadOnlyCollection<string> info) != AfcError.Success || info == null)
            throw new FileNotFoundException(path);

        var stat = ParseFileInfo(info);
        CacheAttr(path, stat);
        return stat;
    }

    /// <summary>
    /// afc_get_file_info returns a flat key/value list, e.g.
    /// [st_size,12345, st_blocks,24, st_nlink,1, st_ifmt,S_IFREG, st_mtime,17000000000000000].
    /// st_mtime is nanoseconds since the epoch.
    /// </summary>
    private static AfcStat ParseFileInfo(IReadOnlyList<string> info)
    {
        long size = 0;
        bool isDir = false;
        DateTime mtime = DateTime.MinValue;

        for (int i = 0; i + 1 < info.Count; i += 2)
        {
            var key = info[i];
            var val = info[i + 1];
            switch (key)
            {
                case "st_size": long.TryParse(val, out size); break;
                case "st_ifmt": isDir = val == "S_IFDIR"; break;
                case "st_mtime":
                    if (long.TryParse(val, out long ns) && ns > 0)
                    {
                        try { mtime = DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000L).LocalDateTime; }
                        catch { mtime = DateTime.MinValue; }
                    }
                    break;
            }
        }
        return new AfcStat(isDir, isDir ? 0 : size, mtime);
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
                fileDesc = new FileDesc { Path = path, IsDirectory = a.IsDirectory };
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
        if (_afc == null) return STATUS_DEVICE_NOT_CONNECTED;

        var path = ToRemote(fileName);
        bool makeDir = (createOptions & FILE_DIRECTORY_FILE) != 0;

        lock (_gate)
        {
            try
            {
                try { _ = GetAttrsCached(path); return STATUS_OBJECT_NAME_COLLISION; }
                catch { /* not found - good */ }

                if (makeDir)
                {
                    if (_afcApi.afc_make_directory(_afc, path) != AfcError.Success)
                        return STATUS_ACCESS_DENIED;
                    var dstat = new AfcStat(true, 0, DateTime.Now);
                    CacheAttr(path, dstat);
                    fileDesc = new FileDesc { Path = path, IsDirectory = true };
                    fileInfo = BuildInfo(dstat);
                }
                else
                {
                    ulong handle = 0;
                    if (_afcApi.afc_file_open(_afc, path, AfcFileMode.FopenWr, ref handle) != AfcError.Success)
                        return STATUS_ACCESS_DENIED;
                    var fstat = new AfcStat(false, 0, DateTime.Now);
                    CacheAttr(path, fstat);
                    fileDesc = new FileDesc { Path = path, IsDirectory = false, Handle = handle, Open = true, Writable = true };
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
        lock (_gate)
        {
            try
            {
                CloseHandle(d);
                ulong handle = 0;
                if (_afc == null || _afcApi.afc_file_open(_afc, d.Path, AfcFileMode.FopenWr, ref handle) != AfcError.Success)
                    return STATUS_ACCESS_DENIED;
                d.Handle = handle; d.Open = true; d.Writable = true;
                InvalidateAttr(d.Path);
                fileInfo = BuildInfo(new AfcStat(false, 0, DateTime.Now));
                return STATUS_SUCCESS;
            }
            catch { return STATUS_ACCESS_DENIED; }
        }
    }

    // ---- AFC handle helpers (caller holds _gate) --------------------------

    private void EnsureOpen(FileDesc d, bool needWrite)
    {
        if (d.Open && (!needWrite || d.Writable)) return;
        CloseHandle(d);
        if (_afc == null) throw new InvalidOperationException("not connected");

        ulong handle = 0;
        var mode = needWrite ? AfcFileMode.FopenRw : AfcFileMode.FopenRdonly;
        var err = _afcApi.afc_file_open(_afc, d.Path, mode, ref handle);
        if (err != AfcError.Success) throw new IOException("afc_file_open failed: " + err);
        d.Handle = handle; d.Open = true; d.Writable = needWrite;
    }

    private void CloseHandle(FileDesc d)
    {
        if (!d.Open) return;
        try { if (_afc != null) _afcApi.afc_file_close(_afc, d.Handle); } catch { }
        d.Open = false; d.Handle = 0; d.Writable = false;
    }

    private void Seek(ulong handle, long offset)
    {
        // whence: SEEK_SET = 0
        _afcApi.afc_file_seek(_afc!, handle, offset, 0);
    }

    // ---- read / write ------------------------------------------------------

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset,
        uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var d = (FileDesc)fileDesc;
        lock (_gate)
        {
            try
            {
                EnsureOpen(d, needWrite: false);
                Seek(d.Handle, (long)offset);

                var managed = new byte[length];
                uint total = 0;
                while (total < length)
                {
                    uint want = length - total;
                    var chunk = new byte[want];
                    uint got = 0;
                    var err = _afcApi.afc_file_read(_afc!, d.Handle, chunk, want, ref got);
                    if (err != AfcError.Success || got == 0) break;
                    Array.Copy(chunk, 0, managed, total, got);
                    total += got;
                }
                if (total == 0) return STATUS_END_OF_FILE;

                Marshal.Copy(managed, 0, buffer, (int)total);
                bytesTransferred = total;
                Interlocked.Add(ref _bytesRead, total);
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
                EnsureOpen(d, needWrite: true);

                // AFC can't report a file length cheaply; for "append" writes we rely
                // on the cached size, and for constrained IO we just honor the offset.
                long writeAt = writeToEndOfFile ? CurrentSize(d) : (long)offset;
                Seek(d.Handle, writeAt);

                var managed = new byte[length];
                Marshal.Copy(buffer, managed, 0, (int)length);

                uint total = 0;
                while (total < length)
                {
                    uint want = length - total;
                    var chunk = new byte[want];
                    Array.Copy(managed, total, chunk, 0, want);
                    uint wrote = 0;
                    var err = _afcApi.afc_file_write(_afc!, d.Handle, chunk, want, ref wrote);
                    if (err != AfcError.Success || wrote == 0) break;
                    total += wrote;
                }

                bytesTransferred = total;
                Interlocked.Add(ref _bytesWritten, total);
                InvalidateAttr(d.Path);
                fileInfo = BuildInfo(new AfcStat(false, writeAt + total, DateTime.Now));
                return STATUS_SUCCESS;
            }
            catch { return STATUS_UNEXPECTED_IO_ERROR; }
        }
    }

    /// <summary>Best-effort current size from the attribute cache / a fresh stat.</summary>
    private long CurrentSize(FileDesc d)
    {
        try { return GetAttrsCached(d.Path).Size; } catch { return 0; }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        return STATUS_SUCCESS; // AFC writes are not buffered on our side
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
        // AFC timestamp setting isn't reliably available; accept silently so copies
        // don't fail (best-effort, same stance as the other engines).
        fileInfo = default;
        if (_readOnly) return STATUS_MEDIA_WRITE_PROTECTED;
        var d = (FileDesc)fileDesc;
        lock (_gate) { try { fileInfo = BuildInfo(GetAttrsCached(d.Path)); } catch { } }
        return STATUS_SUCCESS;
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
                // Truncate-to-zero is the common "overwrite" case - reopen with FopenWr.
                if (newSize == 0)
                {
                    CloseHandle(d);
                    ulong handle = 0;
                    if (_afc != null && _afcApi.afc_file_open(_afc, d.Path, AfcFileMode.FopenWr, ref handle) == AfcError.Success)
                    { d.Handle = handle; d.Open = true; d.Writable = true; }
                }
                InvalidateAttr(d.Path);
                fileInfo = BuildInfo(new AfcStat(false, (long)newSize, DateTime.Now));
                return STATUS_SUCCESS;
            }
            catch { return STATUS_SUCCESS; }
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
                if (_afc != null &&
                    _afcApi.afc_read_directory(_afc, d.Path, out ReadOnlyCollection<string> entries) == AfcError.Success &&
                    entries != null)
                {
                    foreach (var e in entries)
                        if (e is not ("." or "..")) return STATUS_DIRECTORY_NOT_EMPTY;
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
                if (targetExists) { try { _afcApi.afc_remove_path(_afc!, to); } catch { } }

                if (_afc == null || _afcApi.afc_rename_path(_afc, from, to) != AfcError.Success)
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
        if (!_readOnly && (flags & CleanupDelete) != 0)
        {
            lock (_gate)
            {
                try
                {
                    CloseHandle(d);
                    if (_afc != null) _afcApi.afc_remove_path(_afc, d.Path);
                    InvalidateAttr(d.Path);
                }
                catch { }
            }
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        var d = (FileDesc)fileDesc;
        lock (_gate) { CloseHandle(d); }
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

                if (_afc != null &&
                    _afcApi.afc_read_directory(_afc, d.Path, out ReadOnlyCollection<string> names) == AfcError.Success &&
                    names != null)
                {
                    foreach (var name in names)
                    {
                        if (name is "." or ".." or "") continue;
                        var childPath = d.Path == "/" ? "/" + name : d.Path + "/" + name;
                        AfcStat stat;
                        try { stat = GetAttrsCached(childPath); }
                        catch { stat = new AfcStat(false, 0, DateTime.MinValue); }
                        entries.Add(new(name, BuildInfo(stat)));
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

    // ---- security (simplified) ---------------------------------------------

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
        lock (_gate)
        {
            try { _afc?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            _afc = null;
            _device = null;
        }
    }
}
