using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

public class PermissionRisksTests
{
    private const string Folder = @"E:\Repos\App";

    private static PermissionRisk[] Of(string command) => PermissionRisks.OfCommand(command, Folder).ToArray();

    [Theory]
    [InlineData("npm test")]
    [InlineData("git status && git diff --stat")]
    [InlineData("dotnet build 2>&1 | tail -20")]
    [InlineData("Get-ChildItem -Recurse | Select-Object -First 5")]
    [InlineData("git commit -m \"fix; rm the old del key\"")]
    [InlineData("echo done > build.log")]
    [InlineData("dotnet test > NUL")]
    [InlineData("ls > /dev/null")]
    [InlineData("if ($a -gt 1) { $b = $a } ; $x => 1")]
    [InlineData("git rm --cached secrets.txt")]
    [InlineData("git restore --staged src/App.cs")]
    public void A_command_that_risks_nothing_names_nothing(string command)
    {
        Of(command).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("rm -rf build")]
    [InlineData("/bin/rm build/old.txt")]
    [InlineData("sudo rm -r /var/cache/app")]
    [InlineData("rmdir empty")]
    [InlineData("cd src && rm *.tmp")]
    [InlineData("ls | xargs -0 rm")]
    [InlineData("find . -name '*.orig' -delete")]
    [InlineData("find . -name '*.orig' -exec rm {} \\;")]
    [InlineData("git clean -fdx")]
    [InlineData("git rm old.cs")]
    [InlineData("bash -c \"rm -rf node_modules\"")]
    [InlineData("echo $(rm out.txt)")]
    [InlineData("Remove-Item -Recurse -Force .\\bin")]
    [InlineData("Get-ChildItem *.log | ForEach-Object { Remove-Item $_ }")]
    [InlineData("ri out.txt")]
    [InlineData("del /q *.bak")]
    [InlineData("cmd /c rd /s /q obj")]
    [InlineData("pwsh -NoProfile -Command \"Remove-Item x.txt\"")]
    [InlineData("REMOVE-ITEM x.txt")]
    public void A_command_that_deletes_says_so(string command)
    {
        Of(command).ShouldBe([PermissionRisk.DeletesFiles]);
    }

    [Theory]
    [InlineData("git push", new[] { PermissionRisk.Pushes })]
    [InlineData("git -C ../other push origin main", new[] { PermissionRisk.Pushes })]
    [InlineData("git push --force", new[] { PermissionRisk.Pushes, PermissionRisk.RewritesHistory })]
    [InlineData("git push -uf origin main", new[] { PermissionRisk.Pushes, PermissionRisk.RewritesHistory })]
    [InlineData("git push --force-with-lease=main origin", new[] { PermissionRisk.Pushes, PermissionRisk.RewritesHistory })]
    [InlineData("git push origin +main", new[] { PermissionRisk.Pushes, PermissionRisk.RewritesHistory })]
    [InlineData("git reset --hard HEAD~1", new[] { PermissionRisk.DiscardsChanges })]
    [InlineData("git checkout -f main", new[] { PermissionRisk.DiscardsChanges })]
    [InlineData("git restore src/App.cs", new[] { PermissionRisk.DiscardsChanges })]
    [InlineData("git rebase -i HEAD~3", new[] { PermissionRisk.RewritesHistory })]
    [InlineData("git commit --amend --no-edit", new[] { PermissionRisk.RewritesHistory })]
    [InlineData("git add . && git commit -m wip && git push --force", new[] { PermissionRisk.Pushes, PermissionRisk.RewritesHistory })]
    [InlineData("rm -rf dist && git push", new[] { PermissionRisk.DeletesFiles, PermissionRisk.Pushes })]
    public void Pushes_and_history_rewrites_are_named(string command, PermissionRisk[] risks)
    {
        Of(command).ShouldBe(risks);
    }

    [Theory]
    [InlineData(@"echo hi > C:\Windows\hosts.txt")]
    [InlineData(@"echo hi >> ..\other\notes.md")]
    [InlineData("echo hi 2> /c/temp/err.log")]
    [InlineData("echo hi > \"D:\\My Files\\out.txt\"")]
    [InlineData(@"Get-Process | Out-File -FilePath C:\temp\p.txt")]
    [InlineData(@"Set-Content D:\notes.txt 'hello'")]
    [InlineData(@"Add-Content -Path:..\..\log.txt -Value x")]
    [InlineData("echo x | tee out.txt /c/other/out.txt")]
    [InlineData(@"cp build/app.exe C:\Tools\")]
    [InlineData(@"Copy-Item app.exe -Destination C:\Tools")]
    [InlineData("echo x > ~/notes.txt")]
    public void A_command_that_writes_outside_its_folder_says_so(string command)
    {
        Of(command).ShouldBe([PermissionRisk.WritesOutsideItsFolder]);
    }

    [Theory]
    [InlineData("echo hi > out/build.log")]
    [InlineData(@"echo hi > E:\Repos\App\out.txt")]
    [InlineData(@"echo hi > e:\repos\app\sub\out.txt")]
    [InlineData("echo hi > /e/Repos/App/out.txt")]
    [InlineData("echo hi > $unknownVariable/out.txt")]
    [InlineData("cp a.txt b.txt")]
    public void A_write_inside_its_folder_or_where_it_cannot_be_told_names_nothing(string command)
    {
        Of(command).ShouldBeEmpty();
    }

    [Fact]
    public void Without_a_folder_nothing_is_outside_it()
    {
        PermissionRisks.OfCommand(@"echo hi > C:\Windows\x.txt", folder: null).ShouldBeEmpty();
        PermissionRisks.OfWrite(@"C:\Windows\x.txt", folder: null).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"E:\Repos\App\src\App.cs", false)]
    [InlineData(@"E:\Repos\AppOther\src\App.cs", true)]
    [InlineData(@"E:\Repos\App", false)]
    [InlineData(@"C:\Users\me\.claude\settings.json", true)]
    [InlineData(@"E:\Repos\App\..\Secrets\key.txt", true)]
    [InlineData("src/App.cs", false)]
    public void An_edit_outside_its_folder_says_so(string path, bool outside)
    {
        PermissionRisks.OfWrite(path, Folder).ShouldBe(outside ? [PermissionRisk.WritesOutsideItsFolder] : []);
    }

    [Fact]
    public void A_write_that_empties_a_file_says_so()
    {
        PermissionRisks.OfWrite(@"E:\Repos\App\src\App.cs", Folder, emptiesIt: true).ShouldBe([PermissionRisk.EmptiesAFile]);
        PermissionRisks.OfWrite(@"D:\other.cs", Folder, emptiesIt: true).ShouldBe([PermissionRisk.EmptiesAFile, PermissionRisk.WritesOutsideItsFolder]);
    }

    [Fact]
    public void Risks_are_said_as_one_phrase()
    {
        PermissionRisks.Phrase([PermissionRisk.DeletesFiles]).ShouldBe("deletes files");
        PermissionRisks.Phrase([PermissionRisk.DeletesFiles, PermissionRisk.Pushes]).ShouldBe("deletes files and pushes to a remote");
        PermissionRisks.Phrase([PermissionRisk.DeletesFiles, PermissionRisk.Pushes, PermissionRisk.WritesOutsideItsFolder])
            .ShouldBe("deletes files, pushes to a remote and writes outside its folder");
        PermissionRisks.Phrase([]).ShouldBe("");
    }
}
