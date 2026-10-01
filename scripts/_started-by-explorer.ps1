# Exit code 0 when the .cmd wrapper that called this was started by Explorer - a double click, whose
# window closes the moment the wrapper ends. Anything else gives 1: a .cmd started from PowerShell,
# Git Bash, a VS Code task, a scheduled task or a CI step also runs as cmd /c "<file>", but there the
# window stays open or nobody is at the keyboard, and a pause after a good build would wait for ever.
#
# This process's parent is the cmd.exe running the wrapper, and that one's parent is what started it.
$ErrorActionPreference = 'SilentlyContinue'
$self    = Get-CimInstance Win32_Process -Filter "ProcessId = $PID"
$wrapper = if ($self)    { Get-CimInstance Win32_Process -Filter "ProcessId = $($self.ParentProcessId)" }
$starter = if ($wrapper) { Get-CimInstance Win32_Process -Filter "ProcessId = $($wrapper.ParentProcessId)" }
if ($starter -and $starter.Name -ieq 'explorer.exe') { exit 0 }
exit 1
