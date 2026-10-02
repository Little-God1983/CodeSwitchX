using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;

namespace CodeSwitchX.Hosting.VsCode.Companion;

/// <summary>
/// The CodeSwitchX companion extension for VS Code (<c>vscode-companion/</c> in the repository), shipped inside this
/// assembly and packed into a <c>.vsix</c> when it is installed: a zip with the content types, a manifest that names the
/// extension, and the extension's files under <c>extension/</c>. No packaging tool is needed at build time.
/// </summary>
public sealed class CompanionPackage
{
    /// <summary>The id VS Code knows it by: publisher.name.</summary>
    public const string ExtensionId = "codeswitchx.companion";

    private readonly string _packageJson;
    private readonly string _extensionJs;

    private CompanionPackage(string packageJson, string extensionJs)
    {
        _packageJson = packageJson;
        _extensionJs = extensionJs;
        using var package = JsonDocument.Parse(packageJson);
        var root = package.RootElement;
        Version = root.GetProperty("version").GetString()!;
        DisplayName = root.GetProperty("displayName").GetString()!;
        Description = root.GetProperty("description").GetString()!;
        Engine = root.GetProperty("engines").GetProperty("vscode").GetString()!;
    }

    /// <summary>The one this build ships.</summary>
    public static CompanionPackage Shipped { get; } = new(Resource("Companion.package.json"), Resource("Companion.extension.js"));

    /// <summary>The version in its package.json; an installed one that differs is replaced.</summary>
    public string Version { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public string Engine { get; }

    /// <summary>Writes the .vsix to <paramref name="path"/>, replacing any file there.</summary>
    public void WriteVsix(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", ContentTypes);
        Add(zip, "extension.vsixmanifest", Manifest());
        Add(zip, "extension/package.json", _packageJson);
        Add(zip, "extension/extension.js", _extensionJs);
    }

    private const string ContentTypes =
        """<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">"""
        + """<Default Extension=".json" ContentType="application/json"/><Default Extension=".js" ContentType="application/javascript"/>"""
        + """<Default Extension=".vsixmanifest" ContentType="text/xml"/></Types>""";

    internal string Manifest() =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011" xmlns:d="http://schemas.microsoft.com/developer/vsx-schema-design/2011">
          <Metadata>
            <Identity Language="en-US" Id="companion" Version="{Xml(Version)}" Publisher="codeswitchx"/>
            <DisplayName>{Xml(DisplayName)}</DisplayName>
            <Description xml:space="preserve">{Xml(Description)}</Description>
            <Properties>
              <Property Id="Microsoft.VisualStudio.Code.Engine" Value="{Xml(Engine)}"/>
              <Property Id="Microsoft.VisualStudio.Code.ExtensionKind" Value="ui"/>
            </Properties>
          </Metadata>
          <Installation><InstallationTarget Id="Microsoft.VisualStudio.Code"/></Installation>
          <Dependencies/>
          <Assets><Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true"/></Assets>
        </PackageManifest>
        """;

    private static string Xml(string text) => SecurityElement.Escape(text);

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string Resource(string name)
    {
        using var stream = typeof(CompanionPackage).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The companion's {name} is not in this build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
