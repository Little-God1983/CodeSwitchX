using CodeSwitchX.Hosting.VsCode;

namespace CodeSwitchX.Hosting.Tests;

public class VsCodeLocatorTests
{
    [Fact]
    public void Explicit_override_wins()
    {
        var exe = VsCodeLocator.FindExecutable(_ => true, name => name == "CODESWITCHX_VSCODE_EXE" ? @"D:\Tools\Code.exe" : null);

        exe.ShouldBe(@"D:\Tools\Code.exe");
    }

    [Fact]
    public void User_install_is_found_under_LocalAppData()
    {
        var exe = VsCodeLocator.FindExecutable(
            path => path == @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe",
            name => name == "LOCALAPPDATA" ? @"C:\Users\me\AppData\Local" : null);

        exe.ShouldBe(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe");
    }

    [Fact]
    public void Path_entries_with_code_cmd_resolve_to_the_sibling_Code_exe()
    {
        var exe = VsCodeLocator.FindExecutable(
            path => path is @"C:\VSCode\bin\code.cmd" or @"C:\VSCode\Code.exe",
            name => name == "PATH" ? @"C:\Windows;C:\VSCode\bin" : null);

        exe.ShouldBe(@"C:\VSCode\Code.exe");
    }

    [Fact]
    public void Nothing_found_returns_null()
    {
        VsCodeLocator.FindExecutable(_ => false, _ => null).ShouldBeNull();
    }
}
