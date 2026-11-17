@echo off
rem Builds the tester (.NET Framework 4.8) into the dist folder and runs the self test.
cd /d "%~dp0"
dotnet build VA520Tester.csproj -c Release -o dist
if errorlevel 1 exit /b 1
dotnet build tests\SelfTest\SelfTest.csproj -c Release -o tests\SelfTest\bin
if errorlevel 1 exit /b 1
tests\SelfTest\bin\VA520SelfTest.exe
echo.
echo exit code of self test: %errorlevel%
echo Program: dist\VA520Tester.exe
