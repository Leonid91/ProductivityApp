// Builds the release files into a folder (default: release):
//   ProductivityApp-<version>-windows-x64.exe          the single self-contained Windows program
//   ProductivityApp-<version>-macos-apple-silicon.zip  ProductivityApp.app for Apple processors (M1 and later)
//   ProductivityApp-<version>-macos-intel.zip          ProductivityApp.app for Intel processors
//   productivityapp_<version>_amd64.deb / _arm64.deb   Debian and Ubuntu packages (they bring WebKitGTK and Tesseract)
//   ProductivityApp-<version>-linux-x64.tar.gz / -linux-arm64.tar.gz   the same files for other distributions
// The interface must be built first (npm ci and npm run build in ui). Run from the repository root:
//   dotnet run --project tools/Package -- [folder]
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

var root = Directory.GetCurrentDirectory();
var appProject = Path.Combine(root, "src", "Finance.App", "Finance.App.csproj");
if (!File.Exists(appProject)) { Console.Error.WriteLine("Run this from the repository root."); return 1; }
var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(root, "release"));
var version = Regex.Match(File.ReadAllText(appProject), "<Version>([^<]+)</Version>").Groups[1].Value;
var work = Path.Combine(root, "publish", "release-work");
if (Directory.Exists(work)) Directory.Delete(work, true);
Directory.CreateDirectory(output);
const string BundleId = "app.productivity.finances";
var icon = IconPngs(Path.Combine(root, "src", "Finance.App", "Assets", "app.ico"));

// Windows
var win = Publish("net10.0-windows10.0.19041.0", null);
Copy(Path.Combine(win, "ProductivityApp.exe"), $"ProductivityApp-{version}-windows-x64.exe");

// macOS
foreach (var (rid, name) in new[] { ("osx-arm64", "apple-silicon"), ("osx-x64", "intel") })
    MacZip(Publish("net10.0", rid), Path.Combine(output, $"ProductivityApp-{version}-macos-{name}.zip"));

// Linux
foreach (var (rid, arch) in new[] { ("linux-x64", "amd64"), ("linux-arm64", "arm64") })
{
    var dir = Publish("net10.0", rid);
    Deb(dir, arch, Path.Combine(output, $"productivityapp_{version}_{arch}.deb"));
    TarGz(dir, Path.Combine(output, $"ProductivityApp-{version}-{rid}.tar.gz"));
}

Directory.Delete(work, true);
foreach (var f in Directory.EnumerateFiles(output).Order()) Console.WriteLine($"{Path.GetFileName(f),-52} {new FileInfo(f).Length / 1048576.0,7:0.0} MB");
return 0;

string Publish(string framework, string? rid)
{
    var dir = Path.Combine(work, rid ?? "win-x64");
    var arguments = new List<string> { "publish", appProject, "-c", "Release", "-f", framework, "-o", dir, "--nologo" };
    if (rid is not null) arguments.AddRange(["-r", rid]);
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    foreach (var a in arguments) start.ArgumentList.Add(a);
    Console.WriteLine($"publish {rid ?? "win-x64"}");
    using var p = Process.Start(start)!;
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException($"dotnet publish {rid} failed.");
    return dir;
}

void Copy(string from, string name) => File.Copy(from, Path.Combine(output, name), true);

// ------------------------------------------------------------------------------------------------ macOS

void MacZip(string dir, string zipPath)
{
    File.Delete(zipPath);
    const string app = "ProductivityApp.app/";
    // The files of Contents/, signed as a bundle before they are zipped.
    var contents = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["Info.plist"] = Encoding.UTF8.GetBytes(InfoPlist()),
        ["Resources/app.icns"] = Icns(),
    };
    foreach (var f in Directory.EnumerateFiles(dir)) contents["MacOS/" + Path.GetFileName(f)] = File.ReadAllBytes(f);
    MacSign.Bundle(contents, "ProductivityApp", BundleId);
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        foreach (var d in new[] { "", "Contents/", "Contents/MacOS/", "Contents/Resources/", "Contents/_CodeSignature/" }) Entry(zip, app + d, null, 0x41ED); // drwxr-xr-x
        foreach (var (path, data) in contents)
            Entry(zip, app + "Contents/" + path, data, path == "MacOS/ProductivityApp" ? 0x81ED : 0x81A4); // -rwxr-xr-x for the program, -rw-r--r-- the rest
    }
    MadeByUnix(zipPath);
    MacCheck.Zip(zipPath);
}

// Zip writers on Windows mark every entry "made by MS-DOS": macOS (Archive Utility, ditto, unzip) then ignores the Unix
// permissions and the program inside the .app is not executable ("The application can't be opened"). Each entry of
// the central directory is marked "made by Unix" (3) instead, keeping its version.
static void MadeByUnix(string zipPath)
{
    var b = File.ReadAllBytes(zipPath);
    var end = b.Length - 22;
    while (end >= 0 && BitConverter.ToUInt32(b, end) != 0x06054b50) end--;
    if (end < 0) throw new InvalidDataException($"{zipPath}: no end of central directory.");
    int count = BitConverter.ToUInt16(b, end + 10);
    var at = BitConverter.ToUInt32(b, end + 16);
    if (count == 0xFFFF || at == 0xFFFFFFFF) throw new InvalidDataException($"{zipPath}: Zip64 is not handled.");
    for (var i = 0; i < count; i++)
    {
        if (BitConverter.ToUInt32(b, (int)at) != 0x02014b50) throw new InvalidDataException($"{zipPath}: broken central directory.");
        b[at + 5] = 3;
        at += 46u + BitConverter.ToUInt16(b, (int)at + 28) + BitConverter.ToUInt16(b, (int)at + 30) + BitConverter.ToUInt16(b, (int)at + 32);
    }
    File.WriteAllBytes(zipPath, b);
}

void Entry(ZipArchive zip, string name, byte[]? data, int unixMode)
{
    var e = zip.CreateEntry(name, data is null ? CompressionLevel.NoCompression : CompressionLevel.SmallestSize);
    // Unix permissions in the high 16 bits (kept by macOS once the entry is marked made by Unix: see MadeByUnix).
    e.ExternalAttributes = unixMode << 16;
    if (data is null) return;
    using var s = e.Open();
    s.Write(data);
}

string InfoPlist() => $"""
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
    <plist version="1.0">
    <dict>
      <key>CFBundleName</key><string>ProductivityApp</string>
      <key>CFBundleDisplayName</key><string>ProductivityApp</string>
      <key>CFBundleIdentifier</key><string>{BundleId}</string>
      <key>CFBundleExecutable</key><string>ProductivityApp</string>
      <key>CFBundleIconFile</key><string>app.icns</string>
      <key>CFBundlePackageType</key><string>APPL</string>
      <key>CFBundleShortVersionString</key><string>{version}</string>
      <key>CFBundleVersion</key><string>{version}</string>
      <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
      <key>LSApplicationCategoryType</key><string>public.app-category.finance</string>
      <key>LSMinimumSystemVersion</key><string>14.0</string>
      <key>NSHighResolutionCapable</key><true/>
      <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
    </dict>
    </plist>
    """;

// An .icns holding the PNG pictures of the Windows icon (ic07 = 128 px, ic08 = 256 px).
byte[] Icns()
{
    var parts = new List<(string Type, byte[] Png)> { ("ic07", icon[128]), ("ic08", icon[256]) };
    using var m = new MemoryStream();
    void BE(int v) { m.WriteByte((byte)(v >> 24)); m.WriteByte((byte)(v >> 16)); m.WriteByte((byte)(v >> 8)); m.WriteByte((byte)v); }
    m.Write("icns"u8);
    BE(8 + parts.Sum(p => 8 + p.Png.Length));
    foreach (var (type, png) in parts) { m.Write(Encoding.ASCII.GetBytes(type)); BE(8 + png.Length); m.Write(png); }
    return m.ToArray();
}

// The PNG pictures inside the .ico, by width.
static Dictionary<int, byte[]> IconPngs(string ico)
{
    var b = File.ReadAllBytes(ico);
    var result = new Dictionary<int, byte[]>();
    for (var i = 0; i < BitConverter.ToUInt16(b, 4); i++)
    {
        var o = 6 + i * 16;
        int width = b[o] == 0 ? 256 : b[o], size = BitConverter.ToInt32(b, o + 8), offset = BitConverter.ToInt32(b, o + 12);
        result[width] = b[offset..(offset + size)];
    }
    return result;
}

// ------------------------------------------------------------------------------------------------ Linux

void Deb(string dir, string arch, string debPath)
{
    const string prefix = "opt/productivityapp/";
    var files = new List<(string Path, byte[] Data, UnixFileMode Mode)>();
    foreach (var f in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
    {
        var name = Path.GetFileName(f);
        files.Add((prefix + name, File.ReadAllBytes(f), name == "ProductivityApp" ? Exec : Plain));
    }
    files.Add(("usr/share/applications/productivityapp.desktop", Encoding.UTF8.GetBytes(Desktop("/opt/productivityapp/ProductivityApp", "productivityapp")), Plain));
    files.Add(("usr/share/icons/hicolor/128x128/apps/productivityapp.png", icon[128], Plain));
    files.Add(("usr/share/icons/hicolor/256x256/apps/productivityapp.png", icon[256], Plain));

    var data = Tar(w =>
    {
        Add(w, TarEntryType.Directory, "./", null, Exec);
        foreach (var d in Dirs(files.Select(f => f.Path).Append("usr/bin/x"))) Add(w, TarEntryType.Directory, "./" + d + "/", null, Exec);
        foreach (var (path, bytes, mode) in files) Add(w, TarEntryType.RegularFile, "./" + path, bytes, mode);
        Add(w, TarEntryType.SymbolicLink, "./usr/bin/productivityapp", null, Exec, "/opt/productivityapp/ProductivityApp");
    });
    var installedKb = files.Sum(f => (long)f.Data.Length) / 1024 + 1;
    var control = $"""
        Package: productivityapp
        Version: {version}
        Architecture: {arch}
        Maintainer: ProductivityApp
        Installed-Size: {installedKb}
        Depends: libwebkit2gtk-4.1-0, libgtk-3-0, libnotify4, libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67, libssl3 | libssl1.1, zlib1g, ca-certificates, xdg-utils, tesseract-ocr, tesseract-ocr-eng, tesseract-ocr-fra
        Section: misc
        Priority: optional
        Description: Personal finances from bank statements
         Reads bank statements, sorts every operation and draws reports. Works offline.

        """.Replace("\r\n", "\n");
    var md5 = string.Concat(files.Select(f => $"{Convert.ToHexStringLower(MD5.HashData(f.Data))}  {f.Path}\n"));
    var controlTar = Tar(w =>
    {
        Add(w, TarEntryType.Directory, "./", null, Exec);
        Add(w, TarEntryType.RegularFile, "./control", Encoding.UTF8.GetBytes(control), Plain);
        Add(w, TarEntryType.RegularFile, "./md5sums", Encoding.UTF8.GetBytes(md5), Plain);
    });

    // A .deb is an ar archive: debian-binary, control.tar.gz, data.tar.gz.
    using var deb = File.Create(debPath);
    deb.Write("!<arch>\n"u8);
    Ar(deb, "debian-binary", "2.0\n"u8.ToArray());
    Ar(deb, "control.tar.gz", controlTar);
    Ar(deb, "data.tar.gz", data);
}

void TarGz(string dir, string path)
{
    File.WriteAllBytes(path, Tar(w =>
    {
        Add(w, TarEntryType.Directory, "ProductivityApp/", null, Exec);
        foreach (var f in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(f);
            Add(w, TarEntryType.RegularFile, "ProductivityApp/" + name, File.ReadAllBytes(f), name == "ProductivityApp" ? Exec : Plain);
        }
        Add(w, TarEntryType.RegularFile, "ProductivityApp/productivityapp.png", icon[256], Plain);
    }));
}

static string Desktop(string exec, string iconName) => $"""
    [Desktop Entry]
    Type=Application
    Name=ProductivityApp
    Comment=Personal finances from bank statements
    Exec={exec}
    Icon={iconName}
    Terminal=false
    Categories=Office;Finance;
    StartupWMClass=ProductivityApp

    """.Replace("\r\n", "\n");

static IEnumerable<string> Dirs(IEnumerable<string> paths)
{
    var set = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var p in paths)
        for (var i = p.IndexOf('/'); i > 0; i = p.IndexOf('/', i + 1)) set.Add(p[..i]);
    return set;
}

static byte[] Tar(Action<TarWriter> fill)
{
    using var m = new MemoryStream();
    using (var gz = new GZipStream(m, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var w = new TarWriter(gz, TarEntryFormat.Gnu, leaveOpen: true)) fill(w);
    return m.ToArray();
}

static void Add(TarWriter w, TarEntryType type, string name, byte[]? data, UnixFileMode mode, string? link = null)
{
    var e = new GnuTarEntry(type, name)
    {
        Mode = mode, Uid = 0, Gid = 0, UserName = "root", GroupName = "root",
        ModificationTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };
    if (link is not null) e.LinkName = link;
    if (data is not null) e.DataStream = new MemoryStream(data);
    w.WriteEntry(e);
}

static void Ar(Stream s, string name, byte[] data)
{
    var header = $"{name,-16}{0,-12}{0,-6}{0,-6}{"100644",-8}{data.Length,-10}`\n";
    s.Write(Encoding.ASCII.GetBytes(header));
    s.Write(data);
    if (data.Length % 2 == 1) s.WriteByte((byte)'\n');
}

partial class Program
{
    const UnixFileMode Exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    const UnixFileMode Plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
}
