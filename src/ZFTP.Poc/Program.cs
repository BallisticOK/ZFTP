// ============================================================================
//  ZFTP proof-of-concept / command-line test tool.
//  Now built on the shared ZFTP.Core engine (same code the GUI uses).
//
//    ZFTP.Poc --host example.com --port 22 --user me --password secret
//             --key C:\path\id_rsa --root /home/me --drive Z
// ============================================================================

using ZFTP.Core;

static string? Arg(string[] a, string name)
{
    for (int i = 0; i < a.Length - 1; i++)
        if (string.Equals(a[i], "--" + name, StringComparison.OrdinalIgnoreCase))
            return a[i + 1];
    return null;
}

static string Prompt(string label, string? fallback = null)
{
    if (!string.IsNullOrEmpty(fallback)) return fallback;
    Console.Write(label + ": ");
    return Console.ReadLine() ?? "";
}

static string PromptSecret(string label)
{
    Console.Write(label + ": ");
    var sb = new System.Text.StringBuilder();
    ConsoleKeyInfo k;
    while ((k = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
        else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

Console.WriteLine("=== ZFTP (command-line test tool) ===\n");

var profile = new ConnectionProfile
{
    Host = Prompt("SFTP host", Arg(args, "host")),
    Port = int.TryParse(Arg(args, "port"), out var p) ? p : 22,
    Username = Prompt("Username", Arg(args, "user")),
    RemoteRoot = Arg(args, "root") ?? "/",
    DriveLetter = Arg(args, "drive") ?? "Z",
};

string? keyPath = Arg(args, "key");
string? password = Arg(args, "password");
if (!string.IsNullOrEmpty(keyPath))
{
    profile.Auth = AuthMethod.PrivateKey;
    profile.KeyPath = keyPath;
    profile.KeyPassphrase = password ?? "";
}
else
{
    profile.Auth = AuthMethod.Password;
    profile.Password = string.IsNullOrEmpty(password) ? PromptSecret("Password") : password;
}

// --sftptest: connect with raw SSH.NET (no WinFsp) and try to create/write/delete
// a file directly in the remote root. Isolates "can the SSH user write?" from our
// filesystem layer.
if (args.Any(a => string.Equals(a, "--sftptest", StringComparison.OrdinalIgnoreCase)))
{
    var conn = new Renci.SshNet.SftpClient(profile.Host, profile.Port, profile.Username, profile.Password);
    Console.WriteLine($"\n[sftptest] Connecting to {profile.Username}@{profile.Host} ...");
    conn.Connect();
    var root = profile.RemoteRoot == "/" ? conn.WorkingDirectory : profile.RemoteRoot;
    Console.WriteLine($"[sftptest] Remote root: {root}");

    // Pattern A = how a NORMAL client uploads: open once (create), write, close.
    var pathA = root.TrimEnd('/') + "/zftp_test_normal.txt";
    try
    {
        using (var s = conn.Create(pathA))
        {
            var b = System.Text.Encoding.UTF8.GetBytes("normal client write");
            s.Write(b, 0, b.Length);
        }
        Console.WriteLine($"[A normal-client pattern] CREATE+WRITE OK, read: '{conn.ReadAllText(pathA)}'");
        conn.DeleteFile(pathA);
        Console.WriteLine("[A] DELETE OK");
    }
    catch (Exception ex) { Console.WriteLine($"[A normal-client pattern] FAILED ({ex.GetType().Name}): {ex.Message}"); }

    // Pattern B = how ZFTP's MOUNT writes: create empty + close, then REOPEN for
    // ReadWrite and write. If A works but B fails, THIS is the ZFTP bug.
    var pathB = root.TrimEnd('/') + "/zftp_test_mount.txt";
    try
    {
        using (var s = conn.Create(pathB)) { }                       // create empty, close
        Console.WriteLine("[B mount pattern] step1 create-empty OK");
        using (var s = conn.Open(pathB, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite))
        {
            var b = System.Text.Encoding.UTF8.GetBytes("mount-style write");
            s.Write(b, 0, b.Length);
        }
        Console.WriteLine($"[B mount pattern] step2 reopen-ReadWrite+write OK, read: '{conn.ReadAllText(pathB)}'");
        conn.DeleteFile(pathB);
        Console.WriteLine("[B] DELETE OK");
    }
    catch (Exception ex) { Console.WriteLine($"[B mount pattern] FAILED ({ex.GetType().Name}): {ex.Message}  <<< THIS is what ZFTP hits"); }

    conn.Disconnect();
    return;
}

// --servercheck: run read-only diagnostics over SSH to understand the folder's
// ownership/permissions and filesystem type, so we can advise the right fix.
if (args.Any(a => string.Equals(a, "--servercheck", StringComparison.OrdinalIgnoreCase)))
{
    var ssh = new Renci.SshNet.SshClient(profile.Host, profile.Port, profile.Username, profile.Password);
    ssh.Connect();
    var folder = profile.RemoteRoot == "/" ? "$HOME" : profile.RemoteRoot;
    string[] cmds =
    {
        "id",
        $"ls -ld '{folder}'",
        $"stat -c 'owner=%U group=%G perms=%A' '{folder}'",
        $"findmnt -no SOURCE,FSTYPE,OPTIONS --target '{folder}' 2>/dev/null || df -T '{folder}'",
        "sudo -n true 2>&1 && echo 'SUDO: passwordless yes' || echo 'SUDO: needs password or not allowed'",
        $"test -w '{folder}' && echo 'WRITABLE: yes' || echo 'WRITABLE: no'",
    };
    foreach (var c in cmds)
    {
        var r = ssh.RunCommand(c);
        Console.WriteLine($"$ {c}");
        Console.WriteLine($"  {(r.Result + r.Error).Trim()}");
    }
    ssh.Disconnect();
    return;
}

// --fixperms: grant the SSH user full rwx on the remote root via ACLs (additive;
// nothing else loses access). Installs 'acl' first if setfacl is missing.
if (args.Any(a => string.Equals(a, "--fixperms", StringComparison.OrdinalIgnoreCase)))
{
    var ssh = new Renci.SshNet.SshClient(profile.Host, profile.Port, profile.Username, profile.Password);
    ssh.Connect();
    var folder = profile.RemoteRoot;
    var u = profile.Username;
    void Run(string c)
    {
        var r = ssh.RunCommand(c);
        Console.WriteLine($"$ {c}");
        var outp = (r.Result + r.Error).Trim();
        if (!string.IsNullOrEmpty(outp)) Console.WriteLine($"  {outp}");
        Console.WriteLine($"  [exit {r.ExitStatus}]");
    }
    Run("command -v setfacl >/dev/null 2>&1 && echo present || sudo apt-get install -y acl");
    Run($"sudo setfacl -R -m  u:{u}:rwx '{folder}'");
    Run($"sudo setfacl -R -d -m u:{u}:rwx '{folder}'");
    Run($"test -w '{folder}' && echo WRITABLE_YES || echo WRITABLE_NO");
    ssh.Disconnect();
    return;
}

using var session = new MountSession(profile);
session.StateChanged += s => Console.WriteLine($"  [state] {s.State}");

// --speedtest: mount, then print the live transfer counters every second so we
// can see whether BytesRead/BytesWritten actually move during a transfer.
if (args.Any(a => string.Equals(a, "--speedtest", StringComparison.OrdinalIgnoreCase)))
{
    if (!session.Mount()) { Console.WriteLine($"FAILED: {session.LastError}"); return; }
    Console.WriteLine($"Mounted {session.MountPoint}. Watching counters 30s — copy a file to/from it now.");
    long lastR = 0, lastW = 0;
    for (int i = 0; i < 30; i++)
    {
        System.Threading.Thread.Sleep(1000);
        long r = session.BytesRead, w = session.BytesWritten;
        Console.WriteLine($"t={i + 1,2}s  down/s={r - lastR,12}  up/s={w - lastW,12}  totalDown={r,12}  totalUp={w,12}");
        lastR = r; lastW = w;
    }
    session.Unmount();
    return;
}

Console.WriteLine($"\nConnecting to {profile.Username}@{profile.Host}:{profile.Port} ...");
if (!session.Mount())
{
    Console.WriteLine($"FAILED: {session.LastError}");
    return;
}

Console.WriteLine($"\n  Mounted!  Open {session.MountPoint}\\ in Explorer.");
Console.WriteLine("  Press Ctrl+C to unmount and exit.\n");

var done = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
done.Wait();

Console.WriteLine("Unmounting ...");
session.Unmount();
Console.WriteLine("Done. Drive removed.");
