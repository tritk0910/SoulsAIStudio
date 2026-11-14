using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SoulsFormats;

namespace SoulsAiStudio.Core.Formats.Luabnd;

/// <summary>Tool that unpacked a BND4 folder, known from its manifest file name.</summary>
public enum UnpackFormat
{
    /// <summary>WitchyBND: <c>_witchy-bnd4.xml</c>, one shared <c>&lt;root&gt;</c>, explicit DFLT fields.</summary>
    Witchy,
    /// <summary>Yabber: <c>_yabber-bnd4.xml</c>, optional <c>&lt;root&gt;</c> per file, legacy DCX names (<c>DCX_DFLT_11000_44_9</c>).</summary>
    Yabber,
}

/// <summary>
/// The manifest WitchyBND (<c>_witchy-bnd4.xml</c>) or Yabber (<c>_yabber-bnd4.xml</c>) writes next to an unpacked BND4.
/// It records everything needed to repack the folder into an equivalent archive: header fields, DCX compression and
/// each entry's id/flags/path.
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
    IReadOnlyList<WitchyBnd4Manifest.Entry> Files,
    UnpackFormat Kind = UnpackFormat.Witchy)
{
    public const string FileName = "_witchy-bnd4.xml";
    public const string YabberFileName = "_yabber-bnd4.xml";

    /// <param name="Path">Path relative to the unpacked folder, backslash-separated (e.g. <c>out\bin\506300_battle.lua</c>).</param>
    /// <param name="Root">Yabber's per-file root (e.g. <c>N:\</c>); null = the manifest's shared <see cref="WitchyBnd4Manifest.Root"/>.</param>
    public sealed record Entry(Binder.FileFlags Flags, int Id, string Path, string? Root = null);

    /// <summary>Full BND entry name: <c>root\path</c>.</summary>
    public string BndName(Entry e) => Join(e.Root ?? Root, e.Path);

    private static string Join(string root, string path) =>
        string.IsNullOrEmpty(root) ? path : root.TrimEnd('\\') + "\\" + path.TrimStart('\\');

    /// <summary>Manifest file in <paramref name="folder"/> (Witchy preferred over Yabber), or null if it is not an unpacked BND.</summary>
    public static string? FindIn(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        string witchy = Path.Combine(folder, FileName);
        if (File.Exists(witchy)) return witchy;
        string yabber = Path.Combine(folder, YabberFileName);
        return File.Exists(yabber) ? yabber : null;
    }

    public static WitchyBnd4Manifest Load(string xmlPath)
    {
        XElement x = XDocument.Load(xmlPath).Root ?? throw new FormatException($"{xmlPath}: empty xml");
        if (x.Name.LocalName != "bnd4")
            throw new FormatException($"{xmlPath}: root element <{x.Name.LocalName}>, expected <bnd4>");
        var kind = Path.GetFileName(xmlPath).Equals(YabberFileName, StringComparison.OrdinalIgnoreCase)
            ? UnpackFormat.Yabber : UnpackFormat.Witchy;

        string S(string n, string def = "") => (string?)x.Element(n) ?? def;
        int I(string n) => int.Parse(S(n, "0"), CultureInfo.InvariantCulture);
        bool B(string n) => bool.Parse(S(n, "False"));

        XElement files = x.Element("files") ?? throw new FormatException($"{xmlPath}: missing <files>");
        var entries = files.Elements("file").Select(f => new Entry(
            Enum.Parse<Binder.FileFlags>((string?)f.Element("flags") ?? nameof(Binder.FileFlags.Flag1)),
            int.Parse((string?)f.Element("id") ?? "-1", CultureInfo.InvariantCulture),
            (string?)f.Element("path") ?? throw new FormatException($"{xmlPath}: <file> without <path>"),
            (string?)f.Element("root"))).ToList();

        string compression = S("compression", "None");
        DCX.CompressionInfo comp = compression switch
        {
            "DCX_DFLT" => new DCX.DcxDfltCompressionInfo(I("dfltUnk04"), I("dfltUnk10"), I("dfltUnk14"),
                (byte)I("dfltUnk30"), (byte)I("dfltUnk38")),
            "DCX_KRAK" when x.Element("compressionLevel") != null => new DCX.DcxKrakCompressionInfo((byte)I("compressionLevel")),
            "DCX_KRAK" => new DCX.DcxKrakCompressionInfo(DCX.KrakCompressionPreset.EldenRing), // Yabber: no level stored
            "DCX_KRAK_MAX" => new DCX.DcxKrakCompressionInfo(DCX.KrakCompressionPreset.ArmoredCore6),
            "None" or "" => new DCX.NoCompressionInfo(),
            // Yabber / old SoulsFormats names, e.g. DCX_DFLT_11000_44_9 (Sekiro).
            _ when Enum.TryParse<DCX.DfltCompressionPreset>(compression, out var preset) => new DCX.DcxDfltCompressionInfo(preset),
            _ => throw new NotSupportedException($"{xmlPath}: compression '{compression}' is not supported"),
        };

        string ext = S("extended", "0");
        byte extended = ext.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToByte(ext[2..], 16)
            : byte.Parse(ext, CultureInfo.InvariantCulture);

        return new WitchyBnd4Manifest(S("filename"), comp, S("root"), S("version"),
            Enum.Parse<Binder.Format>(S("format")), B("bigendian"), B("bitbigendian"), B("unicode"),
            extended, B("unk04"), B("unk05"), entries, kind);
    }

    /// <summary>Writes a <c>_witchy-bnd4.xml</c> (same element order as WitchyBND, UTF-8 with BOM, CRLF).</summary>
    public void SaveWitchy(string xmlPath)
    {
        var x = new XElement("bnd4", new XElement("filename", Filename));
        switch (Compression)
        {
            case DCX.DcxDfltCompressionInfo d:
                x.Add(new XElement("compression", "DCX_DFLT"),
                    new XElement("dfltUnk04", d.Unk04), new XElement("dfltUnk10", d.Unk10), new XElement("dfltUnk14", d.Unk14),
                    new XElement("dfltUnk30", d.Unk30), new XElement("dfltUnk38", d.Unk38));
                break;
            case DCX.DcxKrakCompressionInfo k:
                x.Add(new XElement("compression", "DCX_KRAK"), new XElement("compressionLevel", k.CompressionLevel));
                break;
            case DCX.NoCompressionInfo:
                x.Add(new XElement("compression", "None"));
                break;
            default:
                throw new NotSupportedException($"Compression {Compression.GetType().Name} can't be written to a Witchy manifest.");
        }
        if (!string.IsNullOrEmpty(Root)) x.Add(new XElement("root", Root));
        x.Add(new XElement("version", Version),
            new XElement("format", Format.ToString()),
            new XElement("bigendian", BigEndian ? "True" : "False"),
            new XElement("bitbigendian", BitBigEndian ? "True" : "False"),
            new XElement("unicode", Unicode ? "True" : "False"),
            new XElement("extended", $"0x{Extended:X2}"),
            new XElement("unk04", Unk04 ? "True" : "False"),
            new XElement("unk05", Unk05 ? "True" : "False"),
            new XElement("files", Files.Select(f => new XElement("file",
                new XElement("flags", f.Flags.ToString()),
                new XElement("id", f.Id.ToString(CultureInfo.InvariantCulture)),
                new XElement("path", f.Path)))));

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(true), NewLineChars = "\r\n" };
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, settings)) new XDocument(new XDeclaration("1.0", "utf-8", null), x).Save(w);
        IO.AtomicFile.WriteAllBytes(xmlPath, ms.ToArray());
    }
}
