using System.Text;
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
    [InlineData("git checkout main")]
    [InlineData("git checkout -b feature/x")]
    [InlineData("echo failed >/dev/stderr")]
    [InlineData("echo failed >&2")]
    [InlineData("echo hi > /dev/tty")]
    [InlineData("cat > notes.md <<'EOF'\nrm -rf /\ngit push --force\nEOF")]
    [InlineData("grep -E \"foo|rm\" log.txt")]
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
    [InlineData("bash -lc \"rm -rf build\"")]
    [InlineData("sh -ec 'rm x'")]
    [InlineData("eval \"rm -rf dist\"")]
    [InlineData("sudo -u deploy rm -rf /srv/app")]
    [InlineData("nice -n 10 rm -rf x")]
    [InlineData("xargs -n 1 rm")]
    [InlineData("env -u FOO rm x")]
    [InlineData("timeout 30 rm -rf x")]
    [InlineData("timeout -s KILL 30 rm -rf x")]
    [InlineData("echo \"$(rm -rf dist)\"")]
    [InlineData("echo \"`rm x`\"")]
    [InlineData("echo it's gone; rm x")]
    public void A_command_that_deletes_says_so(string command)
    {
        Of(command).ShouldBe([PermissionRisk.DeletesFiles]);
    }

    [Theory]
    [InlineData("iex 'Remove-Item -Recurse dist'")]
    [InlineData("Invoke-Expression \"Remove-Item x\"")]
    [InlineData("$r = Remove-Item x")]
    [InlineData("$r=Remove-Item x")]
    [InlineData("Write-Host \"$(Remove-Item x)\"")]
    [InlineData("$s = @'\nit's text, not Remove-Item\n'@\nRemove-Item x")]
    [InlineData("powershell \"Remove-Item x\"")]
    public void A_PowerShell_command_that_deletes_says_so(string command)
    {
        PermissionRisks.OfCommand(command, Folder, dialect: ShellDialect.PowerShell).ShouldBe([PermissionRisk.DeletesFiles]);
    }

    [Theory]
    [InlineData("powershell -NoProfile -ExecutionPolicy Bypass -Command \"Remove-Item -Recurse src\"")]
    [InlineData("powershell -ep Bypass -WindowStyle Hidden \"Remove-Item x\"")]
    [InlineData("pwsh -NoLogo -ExecutionPolicy RemoteSigned -c \"Remove-Item x\"")]
    public void Windows_PowerShell_s_options_and_their_values_are_no_command(string command)
    {
        Of(command).ShouldBe([PermissionRisk.DeletesFiles]);
    }

    [Fact]
    public void A_script_run_with_File_is_not_read_as_a_command()
    {
        Of("powershell -ExecutionPolicy Bypass -File build.ps1 -Clean").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("bash <<'EOF'\nrm -rf /srv/app\ngit push --force\nEOF", new[] { PermissionRisk.DeletesFiles, PermissionRisk.Pushes, PermissionRisk.RewritesHistory })]
    [InlineData("sudo bash <<EOF\nrm -rf /srv/app\nEOF", new[] { PermissionRisk.DeletesFiles })]
    [InlineData("ssh host <<EOF\nrm -rf /srv/app\nEOF", new[] { PermissionRisk.DeletesFiles })]
    [InlineData("echo \"git reset --hard\" | bash", new[] { PermissionRisk.DiscardsChanges })]
    [InlineData("printf 'rm -rf x' | sh -s", new[] { PermissionRisk.DeletesFiles })]
    [InlineData("git commit -m \"Read <<EOF blocks right\"\ngit push --force", new[] { PermissionRisk.Pushes, PermissionRisk.RewritesHistory })]
    [InlineData("x=$((1<<SHIFT))\nrm -rf build", new[] { PermissionRisk.DeletesFiles })]
    [InlineData("cat <<<'here string'\nrm x", new[] { PermissionRisk.DeletesFiles })]
    [InlineData("echo rm -rf x | grep rm", new PermissionRisk[0])]
    [InlineData("echo 'rm -rf x' | bash script.sh", new PermissionRisk[0])]
    public void What_a_shell_is_fed_is_read_as_commands_and_a_lone_here_document_start_hides_nothing(string command, PermissionRisk[] risks)
    {
        Of(command).ShouldBe(risks);
    }

    [Fact]
    public void A_PowerShell_here_string_piped_to_iex_is_read()
    {
        PermissionRisks.OfCommand("@'\nRemove-Item -Recurse x\n'@ | iex", Folder, dialect: ShellDialect.PowerShell).ShouldBe([PermissionRisk.DeletesFiles]);
    }

    [Fact]
    public void An_encoded_command_given_with_ec_is_read()
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("git push --force"));

        Of($"pwsh -NoProfile -ec {encoded}").ShouldBe([PermissionRisk.Pushes, PermissionRisk.RewritesHistory]);
        Of($"powershell -e {encoded}").ShouldBe([PermissionRisk.Pushes, PermissionRisk.RewritesHistory]);
    }

    [Theory]
    [InlineData("git checkout src/App.cs", true)]
    [InlineData("git checkout HEAD~1 src/App.cs", true)]
    [InlineData("git checkout README.md", true)]
    [InlineData("git checkout main", false)]
    [InlineData("git checkout feature/x", false)]
    [InlineData("git checkout -b feature/x origin/feature/x", false)]
    [InlineData("git checkout --track origin/x", false)]
    [InlineData("git checkout v1.2", false)]
    public void A_checkout_of_paths_throws_away_their_changes(string command, bool discards)
    {
        Of(command).ShouldBe(discards ? [PermissionRisk.DiscardsChanges] : []);
    }

    [Fact]
    public void A_checkout_of_a_folder_that_is_there_throws_away_its_changes()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "docs"));

            PermissionRisks.OfCommand("git checkout docs", folder).ShouldBe([PermissionRisk.DiscardsChanges]);
            PermissionRisks.OfCommand("git checkout main", folder).ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("echo x > ${HOME}/../other/out.txt", true)]
    [InlineData("cp build.zip ${TEMP}", true)]
    [InlineData("cd ${NO_SUCH_VARIABLE_107} && echo x > f.txt", false)]
    [InlineData("echo x > ${NO_SUCH_VARIABLE_107}/f.txt", false)]
    public void A_braced_variable_is_one_word(string command, bool outside)
    {
        Of(command).ShouldBe(outside ? [PermissionRisk.WritesOutsideItsFolder] : []);
    }

    [Theory]
    [InlineData("(cd /tmp && tar xzf a.tgz) && echo done > build.log", false)]
    [InlineData("bash -c \"cd /tmp && make\"; echo x > out.log", false)]
    [InlineData("pushd ../lib && make && popd && echo x > log.txt", false)]
    [InlineData(@"Push-Location C:\Temp; Pop-Location; Set-Content log.txt x", false)]
    [InlineData(@"pushd C:\Temp && echo x > log.txt", true)]
    [InlineData("echo $(cd /tmp) > log.txt", false)]
    public void A_cd_in_a_subshell_or_until_popd_moves_only_that_far(string command, bool outside)
    {
        Of(command).ShouldBe(outside ? [PermissionRisk.WritesOutsideItsFolder] : []);
    }

    [Fact]
    public void A_PowerShell_path_ending_in_a_backslash_hides_no_command_after_it()
    {
        PermissionRisks.OfCommand("Copy-Item a \"src\\out\\\"; Remove-Item -Recurse src", Folder, dialect: ShellDialect.PowerShell)
            .ShouldBe([PermissionRisk.DeletesFiles]);
    }

    [Fact]
    public void An_encoded_PowerShell_command_is_read()
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("git push --force"));

        PermissionRisks.OfCommand($"pwsh -NoProfile -EncodedCommand {encoded}", Folder).ShouldBe([PermissionRisk.Pushes, PermissionRisk.RewritesHistory]);
    }

    [Theory]
    [InlineData("git checkout -- src/App.cs")]
    [InlineData("git checkout .")]
    [InlineData("git checkout HEAD -- .")]
    [InlineData("git checkout -p")]
    [InlineData("git stash drop")]
    [InlineData("git stash clear")]
    public void A_command_that_throws_away_uncommitted_changes_says_so(string command)
    {
        Of(command).ShouldBe([PermissionRisk.DiscardsChanges]);
    }

    [Fact]
    public void A_here_document_s_body_is_text_and_what_follows_it_is_read()
    {
        Of("cat > notes.md <<EOF\nIt's done\nEOF\ngit push --force").ShouldBe([PermissionRisk.Pushes, PermissionRisk.RewritesHistory]);
    }

    [Theory]
    [InlineData(@"cd C:\Windows && echo x > hosts.bak", true)]
    [InlineData("cd .. && cd .. && echo x > notes.txt", true)]
    [InlineData("cd src && echo x > out.txt", false)]
    [InlineData("cd - && echo x > ../../out.txt", false)]
    [InlineData(@"Set-Location -Path C:\Temp; Set-Content out.txt x", true)]
    public void A_cd_in_the_command_moves_where_its_relative_writes_land(string command, bool outside)
    {
        Of(command).ShouldBe(outside ? [PermissionRisk.WritesOutsideItsFolder] : []);
    }

    [Fact]
    public void A_chat_that_moved_into_a_subfolder_still_writes_inside_its_project()
    {
        const string sub = @"E:\Repos\App\src";

        PermissionRisks.OfWrite(@"E:\Repos\App\README.md", Folder, cwd: sub).ShouldBeEmpty();
        PermissionRisks.OfCommand("echo x > ../README.md", Folder, cwd: sub).ShouldBeEmpty();
        PermissionRisks.OfCommand("echo x > ../../README.md", Folder, cwd: sub).ShouldBe([PermissionRisk.WritesOutsideItsFolder]);
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
