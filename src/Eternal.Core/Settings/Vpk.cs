using System.Buffers.Binary;
using System.Text;

namespace Eternal.Core.Settings;

/// <summary>
/// Reads single files out of Valve's VPK archives (versions 1 and 2): the
/// directory file lists every entry, and the data sits either right after the
/// listing or in a numbered archive next to it.
/// </summary>
public static class Vpk
{
    private const uint Signature = 0x55AA1234;
    private const int MaxTree = 64 * 1024 * 1024;
    private const int MaxFile = 4 * 1024 * 1024;
    private const ushort InDirectory = 0x7FFF;

    /// <summary>The file's bytes, or null when the archive doesn't have it.</summary>
    public static byte[]? Read(string dirFile, string path)
    {
        path = path.Replace('\\', '/').ToLowerInvariant();
        var slash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        if (dot <= slash)
            return null;
        var (folder, name, extension) = (slash < 0 ? " " : path[..slash], path[(slash + 1)..dot], path[(dot + 1)..]);

        using var stream = File.OpenRead(dirFile);
        Span<byte> header = stackalloc byte[28];
        if (stream.Read(header[..12]) != 12 || BinaryPrimitives.ReadUInt32LittleEndian(header) != Signature)
            throw new InvalidDataException("Not a VPK directory.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var treeSize = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var headerSize = version switch
        {
            1 => 12,
            2 => 28,
            _ => throw new InvalidDataException($"VPK version {version} isn't supported."),
        };
        if (treeSize is <= 0 or > MaxTree)
            throw new InvalidDataException("VPK listing has an odd size.");
        if (version == 2 && stream.Read(header[12..28]) != 16)
            throw new InvalidDataException("VPK header cut short.");

        var tree = new byte[treeSize];
        stream.ReadExactly(tree);
        var reader = new TreeReader(tree);
        while (reader.String() is { Length: > 0 } ext)
        {
            while (reader.String() is { Length: > 0 } dir)
            {
                while (reader.String() is { Length: > 0 } file)
                {
                    reader.UInt32();
                    var preload = reader.UInt16();
                    var archive = reader.UInt16();
                    var offset = reader.UInt32();
                    var length = reader.UInt32();
                    reader.UInt16();
                    var preloaded = reader.Bytes(preload);
                    if (ext != extension || dir != folder || file != name)
                        continue;
                    if (length + (long)preload > MaxFile)
                        throw new InvalidDataException("That file is larger than expected.");

                    var data = new byte[preload + length];
                    preloaded.CopyTo(data);
                    if (length > 0)
                    {
                        using var source = archive == InDirectory
                            ? File.OpenRead(dirFile)
                            : File.OpenRead(ArchivePath(dirFile, archive));
                        source.Position = archive == InDirectory ? headerSize + treeSize + offset : offset;
                        source.ReadExactly(data, preload, (int)length);
                    }
                    return data;
                }
            }
        }
        return null;
    }

    private static string ArchivePath(string dirFile, ushort index)
    {
        var name = Path.GetFileName(dirFile);
        if (!name.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A single-file VPK can't point into another archive.");
        return Path.Combine(Path.GetDirectoryName(dirFile)!, $"{name[..^"dir.vpk".Length]}{index:D3}.vpk");
    }

    private ref struct TreeReader(byte[] data)
    {
        private int _position;

        public string String()
        {
            var end = Array.IndexOf(data, (byte)0, _position);
            if (end < 0)
                throw new InvalidDataException("VPK listing cut short.");
            var text = Encoding.UTF8.GetString(data, _position, end - _position);
            _position = end + 1;
            return text;
        }

        public uint UInt32()
        {
            Need(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_position));
            _position += 4;
            return value;
        }

        public ushort UInt16()
        {
            Need(2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_position));
            _position += 2;
            return value;
        }

        public ReadOnlySpan<byte> Bytes(int count)
        {
            Need(count);
            var span = data.AsSpan(_position, count);
            _position += count;
            return span;
        }

        private readonly void Need(int bytes)
        {
            if (data.Length - _position < bytes)
                throw new InvalidDataException("VPK listing cut short.");
        }
    }
}
