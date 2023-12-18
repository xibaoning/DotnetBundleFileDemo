using System.Buffers;
using System.Text;

namespace DotnetBundleFileDemo;

public class DotNetBundleFile
{
    //https://github.com/dotnet/runtime/blob/main/src/installer/managed/Microsoft.NET.HostModel/AppHost/HostWriter.cs
    // 32 bytes represent the bundle signature: SHA-256 for ".net core bundle"
    private static byte[] _bundleSignature = {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    };

    /// <summary>
    /// 是否NET Bundle文件
    /// </summary>
    /// <param name="source"></param>
    /// <param name="bundleHeaderOffset">header数据偏移</param>
    /// <param name="bundleHeaderPosition">指向header数据偏移</param>
    /// <returns></returns>
    public static bool IsBundle(Stream source, out long bundleHeaderOffset, out long bundleHeaderPosition)
    {
        bundleHeaderOffset = 0;
        bundleHeaderPosition = 0;
        var bundleSignature = new ReadOnlySpan<byte>(_bundleSignature);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bundleSignature.Length);
        try
        {
            source.Position = 0;
            while (source.Position < source.Length - bundleSignature.Length)
            {
                if (source.ReadByte() == bundleSignature[0])
                {
                    source.Position--;
                    source.Read(buffer, 0, bundleSignature.Length);
                    if (bundleSignature.SequenceEqual(new ReadOnlySpan<byte>(buffer, 0, bundleSignature.Length)))
                    {
                        source.Position -= bundleSignature.Length + sizeof(long);
                        bundleHeaderPosition = source.Position;
                        source.Read(buffer, 0, sizeof(long));
                        bundleHeaderOffset = BitConverter.ToInt64(buffer, 0);
                        source.Position = bundleHeaderOffset;
                        return true;
                    }
                }

            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }


        return false;
    }

    /// <summary>
    /// 添加资源
    /// </summary>
    /// <param name="source">bundle源流</param>
    /// <param name="target">目标流</param>
    /// <param name="resource">写入资源流</param>
    /// <param name="resourcePath">写入资源路径</param>
    /// <returns></returns>
    public static bool TryAdd(Stream source, Stream target, Stream resource, string resourcePath)
    {
        if (!IsBundle(source, out var headerOffset, out var headerPosition) || headerOffset == 0) return false;

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        bool result = false;
        try
        {
            source.Position = 0;
            target.Position = 0;
            resource.Position = 0;

            //复制header之前的内容
            var writer = new BinaryWriter(target);
            while (source.Position < headerOffset)
            {
                int readed = source.Read(buffer, 0, (int)(source.Position + buffer.Length < headerOffset ? buffer.Length : headerOffset - source.Position));
                writer.Write(buffer, 0, readed);
            }

            //更新header位置的偏移值: 原始位置+资源长度
            target.Position = headerPosition;
            writer.Write(headerOffset + resource.Length);

            //将资源数据写入到原始header数据所在的位置
            target.Position = headerOffset;
            while (resource.Position < resource.Length)
            {
                int readed = resource.Read(buffer, 0, (int)(resource.Position + buffer.Length < resource.Length ? buffer.Length : resource.Length - resource.Position));
                writer.Write(buffer, 0, readed);
            }

            //将原始header数据追加到资源数据末尾
            while (source.Position < source.Length)
            {
                int readed = source.Read(buffer, 0, (int)(source.Position + buffer.Length < source.Length ? buffer.Length : source.Length - source.Position));
                writer.Write(buffer, 0, readed);
            }

            //写入资源信息
            writer.Write(headerOffset); //Offset
            writer.Write(resource.Length); //Size
            writer.Write((long)0); //CompressedSize
            writer.Write((byte)FileType.Unknown); //type 
            writer.Write(resourcePath); //RelativePath

            //更新打包的文件个数
            target.Position = headerOffset + resource.Length;
            target.Position += sizeof(long);
            target.Read(buffer, 0, sizeof(int));
            int filecount = BitConverter.ToInt32(buffer, 0);
            target.Position -= sizeof(int);
            writer.Write(filecount + 1);
            result = true;
        }
        catch
        {
            result = false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    /// <summary>
    /// 读取文件清单
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    public static IEnumerable<FileEntry> ReadEntryies(Stream source)
    {
        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);

        //bundle版本验证
        uint majorVersion = reader.ReadUInt32();
        uint minorVersion = reader.ReadUInt32();
        if (majorVersion < 1 || majorVersion > 6) new Exception($"此文件的NET版尚未支持:{majorVersion}.{majorVersion}");

        //打包的文件个数
        int fileCount = reader.ReadInt32();
        string bundleID = reader.ReadString();

        //header特殊文件
        if (majorVersion >= 2)
        {
            /*
            此处读取DepsJson与RuntimeConfigJson信息, 本案忽略
            reader.ReadInt64(); //DepsJsonOffset
            reader.ReadInt64(); //DepsJsonSize
            reader.ReadInt64(); //RuntimeConfigJsonOffset
            reader.ReadInt64(); //RuntimeConfigJsonSize
            reader.ReadUInt64(); //Flags
            */

            source.Position += sizeof(long) * 5;
        }

        //打包的文件列表
        for (int i = 0; i < fileCount; i++)
        {
            var entry = ReadEntry(reader, majorVersion);
            yield return entry;
        }
    }
    private static FileEntry ReadEntry(BinaryReader reader, uint bundleMajorVersion)
    {
        var meta = new FileEntry();
        meta.Offset = reader.ReadInt64();
        meta.Size = reader.ReadInt64();
        meta.CompressedSize = bundleMajorVersion >= 6 ? reader.ReadInt64() : 0;
        meta.Type = (FileType)reader.ReadByte();
        meta.RelativePath = reader.ReadString();
        return meta;
    }

    /// <summary>
    /// 获取指定清单信息
    /// </summary>
    /// <param name="source">bundle源</param>
    /// <param name="relativePath">文件路径</param>
    /// <param name="meta"></param>
    /// <returns></returns>
    public static bool TryGetEntry(Stream source, string relativePath, out FileEntry? meta)
    {
        meta = null;
        if (!IsBundle(source, out var headeroffset, out var headerposition)) return false;

        var data = ReadEntryies(source).FirstOrDefault(x => x.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        if (data == null) return false;

        meta = new FileEntry()
        {
            Offset = data.Offset,
            Size = data.Size,
            Type = data.Type,
            CompressedSize = data.CompressedSize,
            RelativePath = data.RelativePath
        };
        return true;
    }

    /// <summary>
    ///  读取嵌入文件流
    /// </summary>
    /// <param name="source">bundle源</param>
    /// <param name="target">写入目标</param>
    /// <param name="relativePath">查找的文件名</param>
    /// <returns></returns>
    public static bool TryRead(Stream source, Stream target, string relativePath)
    {
        if (!IsBundle(source, out var headerOffset, out var headerPosition)) return false;

        var data = ReadEntryies(source).FirstOrDefault(x => x.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        if (data == null) return false;

        bool result = false;
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            source.Position = data.Offset;
            long maxposition = data.Offset + data.Size;
            while (source.Position < maxposition)
            {
                int readed = source.Read(buffer, 0, (int)(source.Position + buffer.Length <= maxposition ? buffer.Length : maxposition - source.Position));
                target.Write(buffer, 0, readed);
            }
            result = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

}

public class FileEntry
{
    /// <summary>
    /// 文件类型
    /// </summary>
    public FileType Type { get; set; }
    /// <summary>
    /// 偏移
    /// </summary>
    public long Offset { get; set; }
    /// <summary>
    /// 大小
    /// </summary>
    public long Size { get; set; }
    /// <summary>
    /// 0表示不压缩
    /// </summary>
    public long CompressedSize { get; set; }
    /// <summary>
    /// 文件相对地址,支持斜杠(文件夹)
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;
}

/// <summary>
/// https://github.com/dotnet/runtime/blob/99cfd79e7c8e8d42bc2c55f6662d64c74cbe8428/src/installer/managed/Microsoft.NET.HostModel/Bundle/FileType.cs
/// FileType: Identifies the type of file embedded into the bundle.
///
/// The bundler differentiates a few kinds of files via the manifest,
/// with respect to the way in which they'll be used by the runtime.
/// </summary>
public enum FileType : byte
{
    Unknown,           // Type not determined.
    Assembly,          // IL and R2R Assemblies
    NativeBinary,      // NativeBinaries
    DepsJson,          // .deps.json configuration file
    RuntimeConfigJson, // .runtimeconfig.json configuration file
    Symbols            // PDB Files
};