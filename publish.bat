@echo off
echo ========================================================
echo Publishing VideoDirector as a Single Executable
echo ========================================================
echo.
echo Bypassing Visual Studio and using MSBuild directly...
echo.

dotnet publish -p:PublishProfile=FolderProfile -p:Platform=x64

echo.
echo ========================================================
echo Publish Complete!
echo Check your target folder: C:\Users\chan_\OneDrive\Apps\VideoDirector
echo ========================================================
pause
