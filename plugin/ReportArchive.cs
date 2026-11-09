using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

// Minimal zip writer/reader: STORED or DEFLATE entries only. DeflateStream lives in System.dll on .NET Framework/Mono
// (no ZipArchive assembly in the pinned Mono allowlist); Mono's codec needs a native zlib helper, so it is probed once.
internal static class ReportArchive
{
    internal const ushort Stored = 0, Deflated = 8;
    internal const long MaxEntryBytes = 16L * ReportExport.MiB + 64 * 1024, MaxInflatedBytes = 48L * ReportExport.MiB;
    internal sealed class Entry
    {
        internal string Name;
        internal byte[] Bytes;
        internal uint Crc, Offset, PackedLength;
        internal ushort Method;
    }
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n => {
        uint c = (uint)n; for (int bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1; return c;
    }).ToArray();
    internal static uint Crc32(byte[] bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte b in bytes) crc = Table[(crc ^ b) & 0xff] ^ (crc >> 8);
        return ~crc;
    }
    private static readonly object probeLock = new object();
    private static bool? deflate;
    // Probe compress+inflate once; any failure (missing MonoPosixHelper, entry point, type init) means STORED.
    internal static bool DeflateAvailable
    {
        get
        {
            lock (probeLock)
            {
                if (deflate == null)
                {
                    try
                    {
                        byte[] sample = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("SoloStone report deflate probe\n", 64)));
                        deflate = Inflate(Compress(sample), sample.Length).SequenceEqual(sample);
                    }
                    catch (Exception) { deflate = false; }
                }
                return deflate.Value;
            }
        }
    }
    internal static byte[] Compress(byte[] bytes)
    {
        using (var packed = new MemoryStream())
        {
            using (var stream = new DeflateStream(packed, CompressionMode.Compress, true)) stream.Write(bytes, 0, bytes.Length);
            return packed.ToArray();
        }
    }
    // Bounded: reads at most size+1 inflated bytes and rejects any length other than the declared size.
    internal static byte[] Inflate(byte[] packed, long size)
    {
        var bytes = new byte[size]; int read = 0, got;
        try
        {
            using (var stream = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                while (read < size && (got = stream.Read(bytes, read, (int)(size - read))) > 0) read += got;
                if (read != size || stream.Read(new byte[1], 0, 1) != 0) throw new IOException("Report entry inflated size mismatch");
            }
        }
        catch (InvalidDataException failure) { throw new IOException("Report entry deflate data invalid", failure); }
        return bytes;
    }
    internal static void Write(Stream target, IList<Entry> entries, bool compress)
    {
        var writer = new BinaryWriter(target, Encoding.UTF8);
        foreach (var entry in entries)
        {
            byte[] name = Encoding.UTF8.GetBytes(entry.Name);
            entry.Offset = checked((uint)target.Position); entry.Crc = Crc32(entry.Bytes);
            // Incompressible entries stay STORED, as other zip writers do.
            byte[] packed = compress ? Compress(entry.Bytes) : entry.Bytes;
            entry.Method = compress && packed.Length < entry.Bytes.Length ? Deflated : Stored;
            if (entry.Method == Stored) packed = entry.Bytes;
            entry.PackedLength = (uint)packed.Length;
            writer.Write(0x04034b50u); writer.Write((ushort)20); writer.Write((ushort)0x800); writer.Write(entry.Method);
            writer.Write((ushort)0); writer.Write((ushort)0x21); writer.Write(entry.Crc);
            writer.Write(entry.PackedLength); writer.Write((uint)entry.Bytes.Length);
            writer.Write((ushort)name.Length); writer.Write((ushort)0); writer.Write(name); writer.Write(packed);
        }
        uint central = checked((uint)target.Position);
        foreach (var entry in entries)
        {
            byte[] name = Encoding.UTF8.GetBytes(entry.Name);
            writer.Write(0x02014b50u); writer.Write((ushort)20); writer.Write((ushort)20); writer.Write((ushort)0x800);
            writer.Write(entry.Method); writer.Write((ushort)0); writer.Write((ushort)0x21); writer.Write(entry.Crc);
            writer.Write(entry.PackedLength); writer.Write((uint)entry.Bytes.Length); writer.Write((ushort)name.Length);
            writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u);
            writer.Write(entry.Offset); writer.Write(name);
        }
        uint size = checked((uint)target.Position - central);
        writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)entries.Count);
        writer.Write((ushort)entries.Count); writer.Write(size); writer.Write(central); writer.Write((ushort)0); writer.Flush();
    }
    internal static bool IsJson(string name) => name == "report.json" || name.EndsWith(".jsonl", StringComparison.Ordinal);
    // Read the actual on-disk bytes, not the pre-write buffers. Only this writer's bounded STORED/DEFLATE format is accepted.
    internal static List<string> Check(string path, ReportRedaction redaction, long zipLimit = ReportExport.MaxZipBytes, long maxInflated = MaxInflatedBytes)
    {
        var hits = new List<string>(); int count = 0; long inflated = 0;
        using (var file = File.OpenRead(path))
        using (var reader = new BinaryReader(file, Encoding.UTF8))
        {
            if (file.Length > zipLimit) throw new IOException("Report size limit exceeded");
            while (reader.ReadUInt32() == 0x04034b50u)
            {
                reader.ReadUInt16(); ushort flags = reader.ReadUInt16(), method = reader.ReadUInt16();
                reader.ReadUInt16(); reader.ReadUInt16(); uint crc = reader.ReadUInt32(), packed = reader.ReadUInt32(), size = reader.ReadUInt32();
                ushort nameSize = reader.ReadUInt16(), extraSize = reader.ReadUInt16();
                if (++count > 16 || flags != 0x800 || (method != Stored && method != Deflated) || (method == Stored && packed != size)
                    || packed > file.Length)
                    throw new IOException("Invalid report archive entry");
                if (size > MaxEntryBytes || (inflated += size) > maxInflated) throw new IOException("Report archive inflated size limit exceeded");
                string name = Encoding.UTF8.GetString(reader.ReadBytes(nameSize)); reader.ReadBytes(extraSize);
                byte[] bytes = reader.ReadBytes((int)packed);
                if (bytes.Length != packed) throw new IOException("Report archive entry truncated");
                if (method == Deflated) bytes = Inflate(bytes, size);
                if (Crc32(bytes) != crc) throw new IOException("Report CRC mismatch");
                // Rule re-check plus the independent identity verifier (literal and \uXXXX forms), kinds/counts only.
                bool json = IsJson(name); string text = Encoding.UTF8.GetString(bytes);
                hits.AddRange(redaction.Check(name).Concat(redaction.Leaks(name)).Select(hit => "entry-name:" + hit));
                hits.AddRange(redaction.Check(text, json).Concat(redaction.Leaks(text, json)).Select(hit => name + ":" + hit));
            }
        }
        if (count == 0) throw new IOException("Empty report archive");
        return hits;
    }
}
