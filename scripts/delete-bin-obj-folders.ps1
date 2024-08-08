$foldersToDelete = @('bin', 'obj')
$foldersToDelete | %{ Get-ChildItem -Path '..' -Filter $_ -Recurse } | Where-Object {$_.PSIsContainer -eq $true} | Remove-Item -Recurse
Write-Output "Deleted all local " | %{$_ + $foldersToDelete + " folders"}