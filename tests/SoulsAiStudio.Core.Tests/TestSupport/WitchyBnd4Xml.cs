using System.Xml.Linq;
using SoulsFormats;

namespace SoulsAiStudio.Core.Tests.TestSupport;

/// <summary>
/// Minimal test-side reader of WitchyBND's <c>_witchy-bnd4.xml</c>, used as an independent
/// oracle for the Core luabnd reader/writer. Not production code.
/// </summary>
public sealed record WitchyBnd4Xml(
    string Filename,
    string Compression,
    int DfltUnk04, int DfltUnk10, int DfltUnk14, byte DfltUnk30, byte DfltUnk38,
    byte CompressionLevel,
    string Root,
    string Version,
    Binder.Format Format,
    bool BigEndian,
    bool BitBigEndian,
    bool Unicode,
    byte Extended,
    bool Unk04,
    bool Unk05,
    IReadOnlyList<WitchyBnd4Xml.FileEntry> Files)
{
    public sealed record FileEntry(Binder.FileFlags Flags, int Id, string Path)
    {
        /// <summary>Full BND entry name = root + "\" + relative path (WitchyBND convention).</summary>
        public string BndName(string root) => string.IsNullOrEmpty(root) ? Path : root.TrimEnd('\\') + "\\" + Path;
    }

    public static WitchyBnd4Xml Load(string xmlPath)
    {
        var x = XDocument.Load(xmlPath).Root ?? throw new FormatException("empty xml");
        if (x.Name.LocalName != "bnd4") throw new FormatException($"root element is <{x.Name}>, expected <bnd4>");

        string S(string n, string def = "") => (string?)x.Element(n) ?? def;
        int I(string n) => int.Parse(S(n, "0"));
        bool B(string n) => bool.Parse(S(n, "False"));

        var files = x.Element("files")!.Elements("file").Select(f => new FileEntry(
            ParseFlags((string?)f.Element("flags") ?? "Flag1"),
            int.Parse((string?)f.Element("id") ?? "-1"),
            (string)f.Element("path")!)).ToList();

        return new WitchyBnd4Xml(
            S("filename"), S("compression"),
            I("dfltUnk04"), I("dfltUnk10"), I("dfltUnk14"), (byte)I("dfltUnk30"), (byte)I("dfltUnk38"),
            (byte)I("compressionLevel"),
            S("root"), S("version"),
            Enum.Parse<Binder.Format>(S("format")),
            B("bigendian"), B("bitbigendian"), B("unicode"),
            Convert.ToByte(S("extended", "0"), S("extended", "0").StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10),
            B("unk04"), B("unk05"),
            files);
    }

    private static Binder.FileFlags ParseFlags(string s) => Enum.Parse<Binder.FileFlags>(s);

    public DCX.CompressionInfo ToCompressionInfo() => Compression switch
    {
        "DCX_DFLT" => new DCX.DcxDfltCompressionInfo(DfltUnk04, DfltUnk10, DfltUnk14, DfltUnk30, DfltUnk38),
        "DCX_KRAK" => new DCX.DcxKrakCompressionInfo(CompressionLevel),
        "None" or "" => new DCX.NoCompressionInfo(),
        _ => throw new NotSupportedException($"compression {Compression} not handled by test oracle"),
    };

    /// <summary>
    /// Reference "repack": build a BND4 from an unpacked WitchyBND folder using only the xml metadata.
    /// This is the behavior the Core luabnd writer must match.
    /// </summary>
    public BND4 BuildBnd4(string unpackedDir)
    {
        var bnd = new BND4
        {
            Version = Version,
            Format = Format,
            BigEndian = BigEndian,
            BitBigEndian = BitBigEndian,
            Unicode = Unicode,
            Extended = Extended,
            Unk04 = Unk04,
            Unk05 = Unk05,
            Compression = ToCompressionInfo(),
        };
        foreach (var f in Files)
        {
            string disk = System.IO.Path.Combine(unpackedDir, f.Path.Replace('\\', System.IO.Path.DirectorySeparatorChar));
            bnd.Files.Add(new BinderFile(f.Flags, f.Id, f.BndName(Root), File.ReadAllBytes(disk)));
        }
        return bnd;
    }
}
