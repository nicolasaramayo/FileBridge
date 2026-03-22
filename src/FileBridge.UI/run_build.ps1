$proc = Start-Process dotnet -ArgumentList "build" -WorkingDirectory "c:\Projects\FileBridge\src\FileBridge.UI" -RedirectStandardOutput "c:\Projects\FileBridge\src\FileBridge.UI\build_output.log" -RedirectStandardError "c:\Projects\FileBridge\src\FileBridge.UI\build_error.log" -PassThru -Wait
echo "Done"
