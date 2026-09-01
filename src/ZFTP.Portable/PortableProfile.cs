namespace ZFTP.Portable;

public sealed class PortableProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Drive";
    public string RemoteName { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public string MountPath { get; set; } = "";
    public bool ReadOnly { get; set; }
    public bool AutoMount { get; set; }

    public string RemoteSpec => string.IsNullOrWhiteSpace(RemotePath)
        ? $"{RemoteName}:"
        : $"{RemoteName}:{RemotePath.TrimStart('/')}";

    public override string ToString() => Name;
}
