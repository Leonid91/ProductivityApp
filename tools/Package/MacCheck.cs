using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

/// <summary>
/// Checks a macOS release zip the way macOS will open it, and stops the build when it would not start:
/// every entry made by Unix (else macOS drops the permissions), the program executable, Info.plist naming it, and every
/// Apple-processor program and library signed with page hashes that match its code (Apple processors run no other).
/// </summary>
static class MacCheck
{
    public static void Zip(string zipPath)
    {
        var problems = new List<string>();
        var raw = File.ReadAllBytes(zipPath);
        foreach (var (name, host) in CentralDirectory(raw))
            if (host != 3) problems.Add($"{name}: made by {host}, not Unix (3)");

        using var zip = ZipFile.OpenRead(zipPath);
        const string contents = "ProductivityApp.app/Contents/";
        var plist = zip.GetEntry(contents + "Info.plist");
        if (plist is null) problems.Add("no Info.plist");
        else
        {
            using var r = new StreamReader(plist.Open());
            var text = r.ReadToEnd();
            var at = text.IndexOf("<key>CFBundleExecutable</key><string>", StringComparison.Ordinal);
            var exe = at < 0 ? null : text[(at + 37)..text.IndexOf("</string>", at + 37, StringComparison.Ordinal)];
            var program = exe is null ? null : zip.GetEntry(contents + "MacOS/" + exe);
            if (program is null) problems.Add("Info.plist names no program of Contents/MacOS");
            else
            {
                if (((program.ExternalAttributes >> 16) & 0b001_001_001) != 0b001_001_001) problems.Add($"{exe} is not executable");
                problems.AddRange(Seal(zip, program, contents));
            }
        }

        foreach (var e in zip.Entries.Where(e => e.Length > 32))
        {
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            var bytes = m.ToArray();
            foreach (var (arch, slice) in Slices(bytes))
                if (arch == "arm64" && Signature(slice) is { } why) problems.Add($"{e.FullName} (arm64): {why}");
        }
        if (problems.Count > 0) throw new InvalidDataException($"{Path.GetFileName(zipPath)} would not open on macOS:\n  " + string.Join("\n  ", problems));
        Console.WriteLine($"checked {Path.GetFileName(zipPath)}: Unix permissions, executable program, signatures");
    }

    /// <summary>
    /// The bundle's seal: the program's signature binds this Info.plist (slot 1) and this CodeResources (slot 3), and
    /// CodeResources holds the right SHA-256 of every other file of Contents/ (what `codesign --verify` checks).
    /// </summary>
    static List<string> Seal(ZipArchive zip, ZipArchiveEntry program, string contents)
    {
        var problems = new List<string>();
        static byte[] Read(ZipArchiveEntry e) { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
        var seal = zip.GetEntry(contents + "_CodeSignature/CodeResources");
        if (seal is null) { problems.Add("no _CodeSignature/CodeResources"); return problems; }
        var resources = Read(seal);
        var m = Read(program);
        var cd = CodeDirectory(m);
        if (cd is null) { problems.Add("the program has no code directory"); return problems; }
        var c = cd.Value.ToArray().AsSpan();
        int hashOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(c[16..]), specials = (int)BinaryPrimitives.ReadUInt32BigEndian(c[24..]);
        if (specials < 3) { problems.Add($"the program's signature binds no sealed files ({specials} special slots)"); return problems; }
        if (!c.Slice(hashOffset - 32, 32).SequenceEqual(SHA256.HashData(Read(zip.GetEntry(contents + "Info.plist")!)))) problems.Add("the signature does not bind this Info.plist");
        if (!c.Slice(hashOffset - 96, 32).SequenceEqual(SHA256.HashData(resources))) problems.Add("the signature does not bind this CodeResources");
        var text = System.Text.Encoding.UTF8.GetString(resources);
        var sealedFiles = System.Text.RegularExpressions.Regex.Matches(text, @"<key>([^<]+)</key>\s*<dict>\s*<key>hash2</key>\s*<data>\s*([^<\s]+)\s*</data>");
        var expected = zip.Entries.Where(e => !e.FullName.EndsWith('/') && e.FullName.StartsWith(contents, StringComparison.Ordinal))
            .Select(e => e.FullName[contents.Length..]).Where(p => p != "Info.plist" && p != "_CodeSignature/CodeResources" && p != "MacOS/" + program.Name).ToHashSet();
        foreach (System.Text.RegularExpressions.Match f in sealedFiles)
        {
            var entry = zip.GetEntry(contents + f.Groups[1].Value);
            if (entry is null) problems.Add($"sealed file missing: {f.Groups[1].Value}");
            else if (Convert.ToBase64String(SHA256.HashData(Read(entry))) != f.Groups[2].Value) problems.Add($"sealed file changed: {f.Groups[1].Value}");
            expected.Remove(f.Groups[1].Value);
        }
        foreach (var p in expected) problems.Add($"file not sealed: {p}");
        return problems;
    }

    /// <summary>The program's code directory (version 2.4 or later), or null.</summary>
    static ReadOnlyMemory<byte>? CodeDirectory(byte[] m)
    {
        var commands = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(16));
        var p = 32;
        for (var i = 0; i < commands; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(p)) == 0x1D)
            {
                var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(p + 8));
                var count = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(offset + 8));
                for (var k = 0; k < count; k++)
                {
                    var at = offset + (int)BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(offset + 16 + k * 8));
                    if (BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at)) == 0xFADE0C02) return m.AsMemory(at, (int)BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(at + 4)));
                }
            }
            p += (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(p + 4));
        }
        return null;
    }

    /// <summary>Each entry of the zip's central directory: its name and the system it says made it.</summary>
    static IEnumerable<(string Name, int Host)> CentralDirectory(byte[] b)
    {
        var end = b.Length - 22;
        while (end >= 0 && BitConverter.ToUInt32(b, end) != 0x06054b50) end--;
        int count = BitConverter.ToUInt16(b, end + 10), at = (int)BitConverter.ToUInt32(b, end + 16);
        for (var i = 0; i < count; i++)
        {
            int nameLength = BitConverter.ToUInt16(b, at + 28);
            yield return (System.Text.Encoding.UTF8.GetString(b, at + 46, nameLength), b[at + 5]);
            at += 46 + nameLength + BitConverter.ToUInt16(b, at + 30) + BitConverter.ToUInt16(b, at + 32);
        }
    }

    /// <summary>The 64-bit Mach-O programs in a file: the file itself, or each one of a universal file.</summary>
    static IEnumerable<(string Arch, byte[] Slice)> Slices(byte[] b)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(b) == 0xCAFEBABE)
        {
            var n = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
            for (var i = 0; i < n; i++)
            {
                var o = 8 + i * 20;
                int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o + 8)), size = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o + 12));
                var slice = b[offset..(offset + size)];
                if (Arch(slice) is { } a) yield return (a, slice);
            }
        }
        else if (Arch(b) is { } a) yield return (a, b);
    }

    static string? Arch(byte[] b) => BinaryPrimitives.ReadUInt32LittleEndian(b) != 0xFEEDFACF ? null
        : BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4)) switch { 0x0100000C => "arm64", 0x01000007 => "x86_64", var c => c.ToString("x") };

    /// <summary>Why the program's signature would be refused, or null when every page hash matches its code.</summary>
    static string? Signature(byte[] m)
    {
        var commands = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(16));
        int p = 32, sigOffset = -1, sigSize = 0;
        for (var i = 0; i < commands; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(p));
            if (cmd == 0x1D) { sigOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(p + 8)); sigSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(p + 12)); }
            p += (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(p + 4));
        }
        if (sigOffset < 0) return "not signed";
        var blob = m.AsSpan(sigOffset, sigSize);
        var count = BinaryPrimitives.ReadUInt32BigEndian(blob[8..]);
        for (var i = 0; i < count; i++)
        {
            var type = BinaryPrimitives.ReadUInt32BigEndian(blob[(12 + i * 8)..]);
            var at = (int)BinaryPrimitives.ReadUInt32BigEndian(blob[(16 + i * 8)..]);
            var cd = blob[at..];
            if ((type != 0 && type != 0x1000) || BinaryPrimitives.ReadUInt32BigEndian(cd) != 0xFADE0C02) continue;
            int hashOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[16..]), pages = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[28..]);
            int limit = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[32..]), hashSize = cd[36], hashType = cd[37], pageSize = 1 << cd[39];
            if (hashType != 2) return $"hash type {hashType} (not SHA-256)";
            if (limit != sigOffset) return "the signature does not cover the whole program";
            for (var k = 0; k < pages; k++)
            {
                var start = k * pageSize;
                var hash = SHA256.HashData(m.AsSpan(start, Math.Min(pageSize, limit - start)));
                if (!hash.AsSpan(0, hashSize).SequenceEqual(cd.Slice(hashOffset + k * hashSize, hashSize))) return $"page {k} does not match its signature";
            }
            return null;
        }
        return "no code directory in the signature";
    }
}
