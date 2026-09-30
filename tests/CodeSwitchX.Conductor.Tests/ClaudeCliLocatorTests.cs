namespace CodeSwitchX.Conductor.Tests;

public sealed class ClaudeCliLocatorTests
{
    private const string Home = @"C:\Users\me";
    private const string Npm = @"C:\Users\me\AppData\Roaming\npm";
    private const string NpmExe = @"C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe";
    private const string Native = @"C:\Users\me\.local\bin\claude.exe";
    private const string Extensions = @"C:\Users\me\.vscode\extensions";

    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _extensions = [];

    private string? Find(string? path) =>
        new ClaudeCliLocator(path, Home, _files.Contains, (folder, _) => folder == Extensions ? _extensions : []).Find();

    [Fact]
    public void A_claude_exe_on_the_PATH_comes_first()
    {
        _files.UnionWith([@"C:\Tools\claude.exe", Native]);

        Find(@"C:\Windows;C:\Tools").ShouldBe(@"C:\Tools\claude.exe");
    }

    [Fact]
    public void The_npm_shim_is_skipped_for_the_exe_it_runs()
    {
        _files.UnionWith([Npm + @"\claude.cmd", NpmExe]);

        Find($"""C:\Windows;"{Npm}" """).ShouldBe(NpmExe);
    }

    [Fact]
    public void The_native_installer_s_folder_is_looked_in_without_the_PATH()
    {
        _files.Add(Native);

        Find(null).ShouldBe(Native);
    }

    [Fact]
    public void Else_the_newest_VS_Code_extension_s_own_claude()
    {
        _extensions.AddRange([Extensions + @"\anthropic.claude-code-2.1.99-win32-x64", Extensions + @"\anthropic.claude-code-2.1.285-win32-x64"]);
        _files.UnionWith(_extensions.Select(e => e + @"\resources\native-binary\claude.exe"));

        Find("").ShouldBe(Extensions + @"\anthropic.claude-code-2.1.285-win32-x64\resources\native-binary\claude.exe");
    }

    [Fact]
    public void Nowhere_is_null()
    {
        _files.Add(Npm + @"\claude.cmd");

        Find(Npm).ShouldBeNull();
    }
}
