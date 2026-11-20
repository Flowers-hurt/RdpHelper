# RdpHelper 卸载时执行：删除本程序接管的防火墙规则，并按当前设置恢复放行，
# 保证卸载后已开启的远程桌面 / SSH 仍然能连（只是不再有 IP 黑白名单）。
# Runs on uninstall: removes RdpHelper's firewall rules and restores plain allow rules
# for whatever Remote Desktop / SSH is currently enabled, so access keeps working.
$ErrorActionPreference = 'SilentlyContinue'

Get-NetFirewallRule -Name 'RdpHelper-RDP-*', 'RdpHelper-SSH-*', 'RdpHelper-Restored-*' | Remove-NetFirewallRule

$ts = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
if ((Get-ItemProperty $ts).fDenyTSConnections -eq 0) {
    $port = (Get-ItemProperty "$ts\WinStations\RDP-Tcp").PortNumber
    if (-not $port) { $port = 3389 }
    if ($port -eq 3389) {
        Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752'
    } else {
        foreach ($p in 'TCP', 'UDP') {
            New-NetFirewallRule -Name "RdpHelper-Restored-RDP-$p" -DisplayName "Remote Desktop ($p $port)" `
                -Direction Inbound -Protocol $p -LocalPort $port -Action Allow -Profile Any | Out-Null
        }
    }
    "RDP allowed on port $port"
}

$svc = Get-Service sshd
if ($svc -and $svc.StartType -ne 'Disabled') {
    $sp = 22
    $cfg = Join-Path $env:ProgramData 'ssh\sshd_config'
    if (Test-Path $cfg) {
        $m = Select-String -Path $cfg -Pattern '^\s*Port\s+(\d+)\s*$' | Select-Object -First 1
        if ($m) { $sp = [int]$m.Matches[0].Groups[1].Value }
    }
    $builtin = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP', 'sshd'
    if ($sp -eq 22 -and $builtin) {
        $builtin | Enable-NetFirewallRule
    } else {
        New-NetFirewallRule -Name 'RdpHelper-Restored-SSH-TCP' -DisplayName "OpenSSH Server (TCP $sp)" `
            -Direction Inbound -Protocol TCP -LocalPort $sp -Action Allow -Profile Any | Out-Null
    }
    "SSH allowed on port $sp"
}
