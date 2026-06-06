$path='C:\Users\macie\.nuget\packages\nefarius.vigem.client\1.19.197\lib\net6.0\Nefarius.ViGEm.Client.dll'
try {
    $asm=[Reflection.Assembly]::LoadFrom($path)
    $asm.GetTypes() | ForEach-Object { Write-Output $_.FullName }
} catch {
    Write-Error $_.Exception.Message
    if ($_.Exception.LoaderExceptions) { $_.Exception.LoaderExceptions | ForEach-Object { Write-Error $_.Message } }
}
