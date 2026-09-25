@echo off
REM ============================================================
REM  Сборка лоадера через MSBuild (Visual Studio 2022, D:\visuak)
REM  Запуск: build.bat   (из папки репозитория, где лежит Loader)
REM ============================================================

set "MSBUILD=D:\visuak\MSBuild\Current\Bin\MSBuild.exe"

if not exist "%MSBUILD%" (
    echo Не найден "%MSBUILD%"
    echo Проверь путь: внутри D:\visuak поищи MSBuild.exe, например так:
    echo   dir /s /b D:\visuak\MSBuild.exe
    pause
    exit /b 1
)

"%MSBUILD%" "Loader\App\Loader.csproj" /p:Configuration=Release /p:Platform=AnyCPU /v:minimal
if errorlevel 1 (
    echo ОШИБКА СБОРКИ
    pause
    exit /b 1
)

echo.
echo ГОТОВО: Loader\App\bin\Release\Loader.exe
pause
