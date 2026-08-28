[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$log = Get-Content "$env:TEMP\core_t3.log" -Encoding UTF8
for ($i = 0; $i -lt $log.Count; $i++) {
  if ($log[$i] -match 'Failed\s+\S+\.\S+') { Write-Output $log[$i].Trim() }
}
Write-Output "=== error snippets ==="
for ($i = 0; $i -lt $log.Count; $i++) {
  if ($log[$i] -match 'Assert\.|Exception :|Expected|error') {
    Write-Output ("{0}: {1}" -f $i, $log[$i].Trim())
  }
}
