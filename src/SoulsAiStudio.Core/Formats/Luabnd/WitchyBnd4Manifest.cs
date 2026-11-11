using System.Globalization;
using System.Xml.Linq;
using SoulsFormats;

namespace SoulsAiStudio.Core.Formats.Luabnd;

/// <summary>
/// The <c>_witchy-bnd4.xml</c> that WitchyBND writes next to an unpacked BND4. It records everything needed to
/// repack the folder into an equivalent archive: header fields, DCX compression and each entry's id/flags/path.
/// </summary>
public sealed record WitchyBnd4Manifest(
    string Filename,
    DCX.CompressionInfo Compression,
    string Root,
    string Version,
    Binder.Format Format,
    bool BigEndian,
    bool BitBigEndian,
    bool Unicode,
    byte Extended,
    bool Unk04,
    bool Unk05,
    IReadOnlyList<WitchyBnd4Manifest.Entry> Files)
{
    public const string FileName = "_witchy-bnd4.xml";

    /// <param name="Path">Path relative to the unpacked folder, backslash-separated (e.g. <c>out\bin\506300_battle.lua</c>).</param>
    public sealed record Entry(Binder.FileFlags Flags, int Id, string Path);

    /// <summary>Full BND entry name: <c>root\path</c> (WitchyBND convention).</summary>
    public string BndName(Entry e) => string.IsNullOrEmpty(Root) ? e.Path : Root.TrimEnd('\\') + "\\" + e.Path;

    public static WitchyBnd4Manifest Load(string xmlPath)
    {
        XElement x = XDocument.Load(xmlPath).Root ?? throw new FormatException($"{xmlPath}: empty xml");
        if (x.Name.LocalName != "bnd4")
            throw new FormatException($"{xmlPath}: root element <{x.Name.LocalName}>, expected <bnd4>");

        string S(string n, string def = "") => (string?)x.Element(n) ?? def;
        int I(string n) => int.Parse(S(n, "0"), CultureInfo.InvariantCulture);
        bool B(string n) => bool.Parse(S(n, "False"));

        XElement files = x.Element("files") ?? throw new FormatException($"{xmlPath}: missing <files>");
        var entries = files.Elements("file").Select(f => new Entry(
            Enum.Parse<Binder.FileFlags>((string?)f.Element("flags") ?? nameof(Binder.FileFlags.Flag1)),
            int.Parse((string?)f.Element("id") ?? "-1", CultureInfo.InvariantCulture),
            (string?)f.Element("path") ?? throw new FormatException($"{xmlPath}: <file> without <path>"))).ToList();

        string compression = S("compression", "None");
        DCX.CompressionInfo comp = compression switch
        {
            "DCX_DFLT" => new DCX.DcxDfltCompressionInfo(I("dfltUnk04"), I("dfltUnk10"), I("dfltUnk14"),
                (byte)I("dfltUnk30"), (byte)I("dfltUnk38")),
            "DCX_KRAK" => new DCX.DcxKrakCompressionInfo((byte)I("compressionLevel")),
            "None" or "" => new DCX.NoCompressionInfo(),
            _ => throw new NotSupportedException($"{xmlPath}: compression '{compression}' is not supported"),
        };

        string ext = S("extended", "0");
        byte extended = ext.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToByte(ext[2..], 16)
            : byte.Parse(ext, CultureInfo.InvariantCulture);

        return new WitchyBnd4Manifest(S("filename"), comp, S("root"), S("version"),
            Enum.Parse<Binder.Format>(S("format")), B("bigendian"), B("bitbigendian"), B("unicode"),
            extended, B("unk04"), B("unk05"), entries);
    }
}
