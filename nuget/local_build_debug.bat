@echo off

dotnet restore ..\src\Spreads.LMDB
dotnet pack ..\src\Spreads.LMDB -c Debug -o C:\transient\LocalNuget  -p:AutoSuffix=True

pause