using System;
using System.Text;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

public static class StreamExtensions
{
    public static string ReadASCII(this Stream stream)
    {
        var builder = new StringBuilder();

        for (int i; (i = stream.ReadByte()) > 0;)
        {
            builder.Append((char)i);
        }

        return builder.ToString();
    }

    private static string DaveEncoding = "\0 #$()-./?0123456789_abcdefghijklmnopqrstuvwxyz~";

    private static string ReadDaveStringImpl(this Stream stream, int bit_index)
    {
        var builder = new StringBuilder();

        int bits = 0;

        var start_pos = stream.Position;

        while (true)
        {
            var byte_index = bit_index / 8;

            stream.Seek(start_pos + byte_index, SeekOrigin.Begin);

            switch (bit_index % 8)
            {
                case 0: // Next: 6
                {
                    bits = stream.ReadByte() & 0x3F;
                } break;

                case 2: // Next: 0
                {
                    bits = stream.ReadByte() >> 2;
                } break;

                case 4: // Next: 2
                {
                    bits = (stream.ReadByte() >> 4) | ((stream.ReadByte() & 0x3) << 4);
                } break;

                case 6: // Next: 4
                {
                    bits = (stream.ReadByte() >> 6) | ((stream.ReadByte() & 0xF) << 2);
                } break;
            }

            bit_index += 6;

            if (bits != 0)
            {
                builder.Append(DaveEncoding[bits]);
            }
            else
            {
                break;
            }
        };

        return builder.ToString();
    }

    public static string ReadDaveString(this Stream stream, string prev_name)
    {
        var start_pos = stream.Position;

        var first = stream.ReadByte();

        if ((first & 0x3F) < 0x38)
        {
            stream.Seek(start_pos, SeekOrigin.Begin);

            return ReadDaveStringImpl(stream, 0);
        }

        if (prev_name == null)
        {
            throw new Exception("Expected Previous Entry");
        }

        var second = stream.ReadByte();

        /*
         * index[0:3] = first[0:3]
         * index[3:5] = first[6:8]
         * index[5:8] = second[0:3]
         */

        var index = (first & 0x7) | ((first & 0xC0) >> 3) | ((second & 0x7) << 5);

        stream.Seek(start_pos, SeekOrigin.Begin);

        var replacement = ReadDaveStringImpl(stream, 12);

        return prev_name.Substring(0, index) + replacement;
    }

    public static string ReadASCII(this Stream stream, int size)
    {
        var buffer = new byte[size];

        stream.Read(buffer, 0, buffer.Length);

        return Encoding.ASCII.GetString(buffer, 0, buffer.TakeWhile(x => x > 0).Count());
    }
}

public abstract class IFileEntry
{
    public string Name { get; protected set; }
    public uint Size { get; protected set; }

    public abstract byte[] Extract(Stream input);

    public IFileEntry(string name, uint size)
    {
        Name = name;
        Size = size;
    }
}

public abstract class IFileReader
{
    public abstract string Identifier { get; }
    public abstract bool IsValid(BinaryReader input);
    public abstract IEnumerable<IFileEntry> Read(BinaryReader input);
}

public class RawFileEntry : IFileEntry
{
    public uint Offset { get; protected set; }

    public RawFileEntry(string name, uint size, uint offset)
        : base(name, size)

    {
        Offset = offset;
    }

    public override byte[] Extract(Stream input)
    {
        input.Seek(Offset, SeekOrigin.Begin);

        var result = new byte[Size];

        if (input.Read(result, 0, result.Length) != Size)
        {
            return null;
        }

        return result;
    }
}

public class DeflatedFileEntry : IFileEntry
{
    public uint Offset { get; protected set; }
    public uint CompressedSize { get; protected set; }

    public DeflatedFileEntry(string name, uint size, uint compressed_size, uint offset)
        : base(name, size)

    {
        CompressedSize = compressed_size;
        Offset = offset;
    }

    public override byte[] Extract(Stream input)
    {
        input.Seek(Offset, SeekOrigin.Begin);

        input = new DeflateStream(input, CompressionMode.Decompress, true);

        var result = new byte[Size];

        if (input.Read(result, 0, result.Length) != Size)
        {
            return null;
        }

        return result;
    }
}

namespace Midtown
{
    public class VirtualFileInode
    {
        public string Name { get; protected set; }
        public uint DataOffset { get; protected set; }
        public uint Size { get; protected set; }
        public bool IsDirectory { get; protected set; }

        public VirtualFileInode(BinaryReader reader, Stream names_stream)
        {
            DataOffset = reader.ReadUInt32();

            var flags_4 = reader.ReadUInt32();
            var flags_8 = reader.ReadUInt32();

            Size        = (flags_4 & 0x7FFFFF);
            IsDirectory = (flags_8 & 1) != 0;

            var name_offset  = (flags_8 >> 14) & 0x3FFFF;
            var ext_offset   = (flags_4 >> 23) & 0x1FF;
            var name_integer = ((flags_8 >> 1) & 0x1FFF).ToString();

            names_stream.Seek(name_offset, SeekOrigin.Begin);

            Name = names_stream.ReadASCII().Replace("\x01", name_integer);

            if (ext_offset != 0)
            {
                names_stream.Seek(ext_offset, SeekOrigin.Begin);

                Name += "." + names_stream.ReadASCII();
            }
        }

        public IFileEntry ToFileEntry(string parent = "")
        {
            if (!IsDirectory)
            {
                return new RawFileEntry(parent + Name, Size, DataOffset);
            }

            return null;
        }

        public IEnumerable<VirtualFileInode> GetChildren(VirtualFileInode[] nodes)
        {
            if (IsDirectory)
            {
                for (var i = DataOffset; i < DataOffset + Size; ++i)
                {
                    yield return nodes[i];
                }
            }
        }

        public IEnumerable<IFileEntry> GetFiles(VirtualFileInode[] nodes, string parent = "")
        {
            if (IsDirectory)
            {
                var name = parent + Name + "\\";

                foreach (var node in GetChildren(nodes))
                {
                    if (node.IsDirectory)
                    {
                        foreach (var file in node.GetFiles(nodes, name))
                        {
                            yield return file;
                        }
                    }
                    else
                    {
                        yield return node.ToFileEntry(name);
                    }
                }
            }
        }
    }

    public class ARESReader : IFileReader
    {
        public static uint ARES_MAGIC = 0x53455241;

        public override string Identifier => "Midtown Madness 1 / ARES";

        public override bool IsValid(BinaryReader input)
        {
            input.BaseStream.Seek(0, SeekOrigin.Begin);

            return input.ReadUInt32() == ARES_MAGIC;
        }

        public override IEnumerable<IFileEntry> Read(BinaryReader input)
        {
            input.BaseStream.Seek(0, SeekOrigin.Begin);

            if (input.ReadUInt32() != ARES_MAGIC)
            {
                yield break;
            }

            var file_count   = input.ReadUInt32();
            var dir_count    = input.ReadUInt32();
            var names_size   = input.ReadUInt32();
            var names_offset = 16 + file_count * 12;

            input.BaseStream.Seek(names_offset, SeekOrigin.Begin);

            var names_stream = new MemoryStream(input.ReadBytes((int) names_size));

            var nodes = new VirtualFileInode[file_count];

            for (var i = 0; i < file_count; ++i)
            {
                input.BaseStream.Seek(16 + (i * 12), SeekOrigin.Begin);

                nodes[i] = new VirtualFileInode(input, names_stream);
            }

            for (var i = 0; i < dir_count; ++i)
            {
                foreach (var file in nodes[i].GetFiles(nodes, ""))
                {
                    yield return file;
                }
            }
        }
    }

    public class DAVEReader : IFileReader
    {
        public static uint DAVE_MAGIC_V1 = 0x45564144; // DAVE
        public static uint DAVE_MAGIC_V2 = 0x65766144; // Dave

        public override string Identifier => "Midtown Madness 2 / DAVE";

        public override bool IsValid(BinaryReader input)
        {
            input.BaseStream.Seek(0, SeekOrigin.Begin);

            var magic = input.ReadUInt32();

            return GetDaveVersion(magic) != -1;
        }

        protected int GetDaveVersion(uint magic)
        {
            if (magic == DAVE_MAGIC_V1)
            {
                return 1;
            }
            else if (magic == DAVE_MAGIC_V2)
            {
                return 2;
            }

            return -1;
        }

        public override IEnumerable<IFileEntry> Read(BinaryReader input)
        {
            input.BaseStream.Seek(0, SeekOrigin.Begin);

            var magic = input.ReadUInt32();

            var version = GetDaveVersion(magic);

            if (version == -1)
            {
                throw new Exception("Invalid Archive");
            }

            var file_count   = input.ReadUInt32();
            var names_offset = input.ReadUInt32();
            var names_size   = input.ReadUInt32();

            input.BaseStream.Seek(2048 + names_offset, SeekOrigin.Begin);
            var names_stream = new MemoryStream(input.ReadBytes((int) names_size));

            string prev_name = null;

            for (var i = 0; i < file_count; ++i)
            {
                input.BaseStream.Seek(2048 + (i * 16), SeekOrigin.Begin);

                var name_offset = input.ReadUInt32();
                var data_offset = input.ReadUInt32();

                var size            = input.ReadUInt32();
                var compressed_size = input.ReadUInt32();

                names_stream.Seek(name_offset, SeekOrigin.Begin);

                string name;
                if (version == 1)
                {
                    name = names_stream.ReadASCII();
                }
                else if (version == 2)
                {
                    name = names_stream.ReadDaveString(prev_name);

                    prev_name = name;
                }
                else
                {
                    throw new Exception("Invalid Version");
                }

                if (size != compressed_size)
                {
                    yield return new DeflatedFileEntry(name, size, compressed_size, data_offset);
                }
                else
                {
                    yield return new RawFileEntry(name, size, data_offset);
                }
            }
        }
    }

    public class ZipReader : IFileReader
    {
        public static uint ZIPENDLOCATOR_MAGIC = 0x06054B50;

        public override string Identifier => "ZIP / PK";

        public override bool IsValid(BinaryReader input)
        {
            input.BaseStream.Seek(-22, SeekOrigin.End);

            return input.ReadUInt32() == ZIPENDLOCATOR_MAGIC; // ZIPENDLOCATOR
        }

        public override IEnumerable<IFileEntry> Read(BinaryReader input)
        {
            input.BaseStream.Seek(-22, SeekOrigin.End);

            if (input.ReadUInt32() != ZIPENDLOCATOR_MAGIC)
            {
                yield break;
            }

            var diskNumber = input.ReadUInt16();
            var startDiskNumber = input.ReadUInt16();

            if (diskNumber != startDiskNumber)
            {
                throw new Exception("Incomplete Archive");
            }

            var fileCount = input.ReadUInt16();
            var filesInDirectory = input.ReadUInt16();

            var directorySize = input.ReadUInt32();
            var directoryOffset = input.ReadUInt32();

            var fileCommentLength = input.ReadUInt16();

            var currentOffset = directoryOffset;

            while (true)
            {
                input.BaseStream.Seek(currentOffset, SeekOrigin.Begin);

                if (input.ReadUInt32() != 0x02014B50) // ZIPDIRENTRY
                {
                    break;
                }

                var versionMadeBy = input.ReadUInt16();
                var versionToExtract = input.ReadUInt16();
                var flags = input.ReadUInt16();

                var compressionMethod = input.ReadUInt16();

                var fileTime = input.ReadUInt16();
                var fileDate = input.ReadUInt16();
                var crc = input.ReadUInt32();

                var compressedSize = input.ReadUInt32();
                var uncompressedSize = input.ReadUInt32();

                var nameLength = input.ReadUInt16();
                var extraLength = input.ReadUInt16();
                var commentLength = input.ReadUInt16();

                var diskNumberStart = input.ReadUInt16();

                var internalAttributes = input.ReadUInt16();
                var externalAttributes = input.ReadUInt32();

                var recordOffset = input.ReadUInt32(); // ZIPFILERECORD

                var name = input.BaseStream.ReadASCII(nameLength);

                currentOffset = (uint) input.BaseStream.Position + extraLength + commentLength;

                var dataOffset = recordOffset + 30 + nameLength;

                if (compressionMethod == 0)
                {
                    yield return new RawFileEntry(name, uncompressedSize, dataOffset);
                }
                else if (compressionMethod == 8)
                {
                    yield return new DeflatedFileEntry(name, uncompressedSize, compressedSize, dataOffset);
                }
                else
                {
                    throw new Exception($"Invalid compression method: {compressionMethod} (expected 0 or 8)");
                }
            }
        }
    }

    public class PKG3Reader : IFileReader
    {
        public static uint PKG3_MAGIC = 0x33474B50; // PKG3
        public static uint FILE_MAGIC = 0x454C4946; // FILE

        public override string Identifier => "modPackage 3 / PKG3";

        public override bool IsValid(BinaryReader input)
        {
            input.BaseStream.Seek(0, SeekOrigin.Begin);

            return input.ReadUInt32() == PKG3_MAGIC;
        }

        public override IEnumerable<IFileEntry> Read(BinaryReader input)
        {
            input.BaseStream.Seek(0, SeekOrigin.Begin);

            if (input.ReadUInt32() != PKG3_MAGIC)
            {
                yield break;
            }

            var currentOffset = 4;

            while (currentOffset < input.BaseStream.Length)
            {
                input.BaseStream.Seek(currentOffset, SeekOrigin.Begin);

                if (input.ReadUInt32() != FILE_MAGIC)
                {
                    yield break;
                }

                var nameSize = input.ReadByte();

                var name = input.BaseStream.ReadASCII(nameSize);
                var size = input.ReadUInt32();

                currentOffset = (int)(input.BaseStream.Position + size);

                yield return new RawFileEntry(name, size, (uint) input.BaseStream.Position);
            }
        }
    }
}

namespace MidtownExtractor
{
    class Program
    {
        static IFileReader[] FileReaders =
        {
            new Midtown.ARESReader(),
            new Midtown.DAVEReader(),
            new Midtown.PKG3Reader(),
            new Midtown.ZipReader(),
        };

        static string ConsoleClearLine()
        {
            return string.Format("\r{0}\r", new string(' ', Console.WindowWidth - 1));
        }

        static void OverwriteConsole(string format, params object[] args)
        {
            Console.Write(ConsoleClearLine() + format, args);
        }

        static void OverwriteConsoleLine(string format, params object[] args)
        {
            Console.WriteLine(ConsoleClearLine() + format, args);
        }

        static void ExtractFiles(Stream input, IFileEntry[] files, string path)
        {
            using (var fileStream = File.OpenRead(path))
            {
                var fileLock = new object();

                var stopwatch = Stopwatch.StartNew();

                var tasks = files.Select((entry) =>
                {
                    try
                    {
                        return new Task(() =>
                        {
                            byte[] file_data;

                            lock (fileLock)
                            {
                                file_data = entry.Extract(fileStream);
                            }

                            if (file_data?.Length > 0)
                            {
                                var outputName = Path.Combine(Path.GetDirectoryName(path), "Extracted", Path.GetFileNameWithoutExtension(path), entry.Name);

                                Directory.CreateDirectory(Path.GetDirectoryName(outputName));

                                using (var output = File.OpenWrite(outputName))
                                {
                                    output.Write(file_data, 0, file_data.Length);
                                }
                            }
                        });
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Failed to extract {0} - {1}", entry.Name, e);
                    }

                    return null;
                }).ToArray();

                foreach (var task in tasks)
                {
                    task.Start();
                }

                var endTask = Task.WhenAll(tasks);

                while (!endTask.Wait(100))
                {
                    var completed = tasks.Where(x => x.IsCompleted).ToArray();

                    OverwriteConsole("{0} : {1} / {2}", path, completed.Length, tasks.Length);
                }

                OverwriteConsoleLine("{0} : {1} files in {2} ms", path, tasks.Length, stopwatch.ElapsedMilliseconds);
            }
        }

        static void ExtractArchive(string path)
        {
            using (var fileStream = File.OpenRead(path))
            {
                var fileReader = new BinaryReader(fileStream);

                foreach (var reader in FileReaders)
                {
                    if (reader.IsValid(fileReader))
                    {
                        Console.WriteLine("Extracting {0} ({1})", Path.GetFileName(path), reader.Identifier);

                        var files = reader.Read(fileReader).ToArray();

                        ExtractFiles(fileStream, files, path);

                        break;
                    }
                }
            }
        }

        static string GetArgOrReadLine(string name, string[] args, uint index)
        {
            if (index < args.Length)
            {
                return args[index];
            }

            Console.Write("{0}: ", name);

            return Console.ReadLine();
        }

        static string GetArgOrDefault(string[] args, uint index, string defaultValue)
        {
            return index < args.Length ? args[index] : defaultValue;
        }

        static void Main(string[] args)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Green;

            var path = GetArgOrReadLine("Path", args, 0);

            if (File.Exists(path))
            {
                ExtractArchive(path);
            }
            else if (Directory.Exists(path))
            {
                var extension = GetArgOrReadLine("Extension", args, 1);

                foreach (var file in Directory.GetFiles(path, "*." + extension /*, SearchOption.AllDirectories */))
                {
                    ExtractArchive(file);
                }
            }
            else
            {
                Console.WriteLine("Invalid file/path: {0}", path);
            }

            Console.WriteLine("Finished");

            Console.ReadKey(true);
        }
    }
}
