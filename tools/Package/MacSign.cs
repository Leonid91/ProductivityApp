using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Signs a macOS .app the way `codesign --sign -` does (an ad-hoc signature: no certificate, nothing that costs money),
/// from any system. The .NET build signs the program alone ("apphost"): inside a bundle, macOS then finds a signature
/// that names no Info.plist and no sealed files, and refuses it ("code has no resources but signature indicates they
/// must be present"). Here every file of Contents/ is sealed in _CodeSignature/CodeResources, and the program's
/// signature is made again with the bundle's identifier, binding Info.plist and that seal.
/// </summary>
static class MacSign
{
    /// <summary>
    /// Signs the bundle given as its files under Contents/ (paths like "MacOS/x.dll"); adds _CodeSignature/CodeResources
    /// and replaces the program's bytes with the signed ones.
    /// </summary>
    public static void Bundle(SortedDictionary<string, byte[]> contents, string executable, string identifier)
    {
        var program = "MacOS/" + executable;
        var resources = Encoding.UTF8.GetBytes(CodeResources(contents.Where(f => f.Key != program && f.Key != "Info.plist")));
        contents["_CodeSignature/CodeResources"] = resources;
        contents[program] = Sign(contents[program], identifier, contents["Info.plist"], resources);
    }

    /// <summary>The seal of the bundle's files: their SHA-1 (old rules, Resources only) and SHA-256 (every file).</summary>
    static string CodeResources(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        var list = files.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        var s = new StringBuilder();
        s.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n");
        s.Append("\t<key>files</key>\n\t<dict>\n");
        foreach (var (path, data) in list.Where(f => f.Key.StartsWith("Resources/", StringComparison.Ordinal)))
            s.Append($"\t\t<key>{path}</key>\n\t\t<data>\n\t\t{Convert.ToBase64String(SHA1.HashData(data))}\n\t\t</data>\n");
        s.Append("\t</dict>\n\t<key>files2</key>\n\t<dict>\n");
        // Files of Contents/MacOS are sealed by their content, like any file (the libraries keep their own signatures).
        foreach (var (path, data) in list)
            s.Append($"\t\t<key>{path}</key>\n\t\t<dict>\n\t\t\t<key>hash2</key>\n\t\t\t<data>\n\t\t\t{Convert.ToBase64String(SHA256.HashData(data))}\n\t\t\t</data>\n\t\t</dict>\n");
        s.Append("\t</dict>\n");
        s.Append("""
            	<key>rules</key>
            	<dict>
            		<key>^Resources/</key>
            		<true/>
            		<key>^version.plist$</key>
            		<true/>
            	</dict>
            	<key>rules2</key>
            	<dict>
            		<key>^.*</key>
            		<true/>
            		<key>^Info\.plist$</key>
            		<dict>
            			<key>omit</key>
            			<true/>
            			<key>weight</key>
            			<real>20</real>
            		</dict>
            		<key>^PkgInfo$</key>
            		<dict>
            			<key>omit</key>
            			<true/>
            			<key>weight</key>
            			<real>20</real>
            		</dict>
            		<key>^Resources/</key>
            		<dict>
            			<key>weight</key>
            			<real>20</real>
            		</dict>
            	</dict>
            </dict>
            </plist>

            """);
        return s.ToString();
    }

    const uint CodeDirectoryMagic = 0xFADE0C02, RequirementsMagic = 0xFADE0C01, SuperBlobMagic = 0xFADE0CC0;

    /// <summary>
    /// The program (a 64-bit Mach-O already signed) signed again ad hoc for its bundle: special slots 1 (Info.plist),
    /// 2 (requirements) and 3 (CodeResources) hashed, every page hashed again. The signature stays the last thing of the
    /// file; __LINKEDIT and the signature command are resized to it.
    /// </summary>
    public static byte[] Sign(byte[] input, string identifier, byte[] infoPlist, byte[] codeResources)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(input) != 0xFEEDFACF) throw new InvalidDataException("The program is not a 64-bit Mach-O.");
        var m = (byte[])input.Clone();
        int commands = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(16)), p = 32, signatureCommand = -1, linkedit = -1;
        for (var i = 0; i < commands; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(p));
            if (cmd == 0x1D) signatureCommand = p;
            if (cmd == 0x19 && Encoding.ASCII.GetString(m, p + 8, 16).TrimEnd('\0') == "__LINKEDIT") linkedit = p;
            p += (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(p + 4));
        }
        if (signatureCommand < 0 || linkedit < 0) throw new InvalidDataException("The program has no signature to replace.");
        var dataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(signatureCommand + 8));
        var oldSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(signatureCommand + 12));
        if (dataOffset + oldSize != m.Length) throw new InvalidDataException("The program's signature is not at its end.");

        // The executable segment as the old signature described it.
        var old = OldCodeDirectory(m.AsSpan(dataOffset, oldSize));
        var execBase = BinaryPrimitives.ReadUInt64BigEndian(old[64..]);
        var execLimit = BinaryPrimitives.ReadUInt64BigEndian(old[72..]);
        var execFlags = BinaryPrimitives.ReadUInt64BigEndian(old[80..]);
        var pageShift = old[39];

        // Sizes first: the header changes with them, and the pages are hashed after.
        var requirements = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(requirements, RequirementsMagic);
        BinaryPrimitives.WriteUInt32BigEndian(requirements.AsSpan(4), 12);
        var ident = Encoding.UTF8.GetBytes(identifier + "\0");
        const int special = 3, header = 88, hashSize = 32;
        var pageSize = 1 << pageShift;
        var codeSlots = (dataOffset + pageSize - 1) / pageSize;
        var hashOffset = header + ident.Length + special * hashSize;
        var cdLength = hashOffset + codeSlots * hashSize;
        var superLength = 12 + 3 * 8 + cdLength + requirements.Length + 8;
        var newSize = (superLength + 15) / 16 * 16;

        Array.Resize(ref m, dataOffset + newSize);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(signatureCommand + 12), (uint)newSize);
        var segmentOffset = BinaryPrimitives.ReadUInt64LittleEndian(m.AsSpan(linkedit + 40));
        var fileSize = (ulong)m.Length - segmentOffset;
        BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(linkedit + 48), fileSize);
        var vmSize = BinaryPrimitives.ReadUInt64LittleEndian(m.AsSpan(linkedit + 32));
        if (vmSize < fileSize) BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(linkedit + 32), (fileSize + 0x3FFF) / 0x4000 * 0x4000);

        var cd = new byte[cdLength];
        var c = cd.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(c, CodeDirectoryMagic);
        BinaryPrimitives.WriteUInt32BigEndian(c[4..], (uint)cdLength);
        BinaryPrimitives.WriteUInt32BigEndian(c[8..], 0x20400);
        BinaryPrimitives.WriteUInt32BigEndian(c[12..], 0x2); // ad hoc
        BinaryPrimitives.WriteUInt32BigEndian(c[16..], (uint)hashOffset);
        BinaryPrimitives.WriteUInt32BigEndian(c[20..], header);
        BinaryPrimitives.WriteUInt32BigEndian(c[24..], special);
        BinaryPrimitives.WriteUInt32BigEndian(c[28..], (uint)codeSlots);
        BinaryPrimitives.WriteUInt32BigEndian(c[32..], (uint)dataOffset);
        c[36] = hashSize; c[37] = 2; c[38] = 0; c[39] = pageShift; // SHA-256, pages of 2^pageShift
        BinaryPrimitives.WriteUInt64BigEndian(c[64..], execBase);
        BinaryPrimitives.WriteUInt64BigEndian(c[72..], execLimit);
        BinaryPrimitives.WriteUInt64BigEndian(c[80..], execFlags);
        ident.CopyTo(c[header..]);
        SHA256.HashData(infoPlist).CopyTo(c[(hashOffset - 1 * hashSize)..]);
        SHA256.HashData(requirements).CopyTo(c[(hashOffset - 2 * hashSize)..]);
        SHA256.HashData(codeResources).CopyTo(c[(hashOffset - 3 * hashSize)..]);
        for (var k = 0; k < codeSlots; k++)
        {
            var start = k * pageSize;
            SHA256.HashData(m.AsSpan(start, Math.Min(pageSize, dataOffset - start))).CopyTo(c[(hashOffset + k * hashSize)..]);
        }

        var s = m.AsSpan(dataOffset, newSize);
        s.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(s, SuperBlobMagic);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)superLength);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], 3);
        // The blobs in order: code directory (type 0), requirements (type 2), empty signature wrapper (type 0x10000).
        var at = 12 + 3 * 8;
        (uint Type, byte[] Blob)[] blobs = [(0, cd), (2, requirements), (0x10000, [0xFA, 0xDE, 0x0B, 0x01, 0, 0, 0, 8])];
        for (var i = 0; i < blobs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(s[(12 + i * 8)..], blobs[i].Type);
            BinaryPrimitives.WriteUInt32BigEndian(s[(16 + i * 8)..], (uint)at);
            blobs[i].Blob.CopyTo(s[at..]);
            at += blobs[i].Blob.Length;
        }
        return m;
    }

    static ReadOnlySpan<byte> OldCodeDirectory(ReadOnlySpan<byte> superBlob)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(superBlob[8..]);
        for (var i = 0; i < count; i++)
        {
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(superBlob[(16 + i * 8)..]);
            var blob = superBlob[offset..];
            if (BinaryPrimitives.ReadUInt32BigEndian(blob) == CodeDirectoryMagic && BinaryPrimitives.ReadUInt32BigEndian(blob[8..]) >= 0x20400) return blob;
        }
        throw new InvalidDataException("The program's signature has no code directory of version 2.4.");
    }
}
